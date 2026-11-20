using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;

namespace Continuum.Infrastructure.Privacy;

/// <summary>
/// Набор исключений privacy-конвейера: приложения и директории.
/// Спецификация: docs/specs/privacy-pipeline.md, §4.
/// Дефолты (менеджеры паролей, собственное приложение, системные хранилища
/// секретов) неудаляемы: набор всегда содержит их, пользовательские записи
/// добавляются поверх.
/// С шага 9 набор меняется на лету (<see cref="Apply"/>): исключение,
/// добавленное в настройках, действует без перезапуска (ФТ-2.3).
/// Чтение и запись идут с разных потоков (конвейер/UI), поэтому состояние -
/// неизменяемый снимок за volatile-ссылкой: читатель видит либо старый
/// снимок целиком, либо новый, промежуточных нет.
/// Все сравнения регистронезависимые.
/// </summary>
public sealed class ExclusionSet : IExclusionSet
{
    // §4.1: менеджеры паролей + собственное приложение - не наблюдаются никогда
    private static readonly string[] DefaultApplications =
    [
        "1password", "keepass", "bitwarden", "enpass", "dashlane",
        "lastpass", "nordpass", "stickypasswords", "keeper",
        "continuum", // собственное окно исключается безусловно
    ];

    private State _current;

    /// <summary>Снимок набора: всё, что нужно проверкам, в неизменяемом виде.</summary>
    private sealed class State
    {
        public required HashSet<string> ApplicationNames { get; init; }

        public required HashSet<string> ApplicationExePaths { get; init; }

        public required string[] Directories { get; init; }

        public required NeverRecordRule[] Rules { get; init; }
    }

    public ExclusionSet(IEnumerable<string>? extraApplications = null, IEnumerable<string>? extraDirectories = null)
    {
        _current = BuildState(extraApplications ?? [], extraDirectories ?? [], []);
    }

    /// <summary>
    /// Применяет пользовательские настройки поверх дефолтов: исключения
    /// приложений и каталогов, правила «не записывать никогда».
    /// Вызывается при загрузке настроек и после каждого сохранения.
    /// </summary>
    public void Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var next = BuildState(
            settings.ExcludedApplications,
            settings.ExcludedDirectories,
            settings.NeverRecordRules);
        Volatile.Write(ref _current, next);
    }

    public bool IsApplicationExcluded(string applicationName, string? exePath)
    {
        var state = Volatile.Read(ref _current);

        // По нормализованному имени процесса (без учёта регистра, без расширения)
        if (!string.IsNullOrWhiteSpace(applicationName)
            && state.ApplicationNames.Contains(StripExtension(applicationName)))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(exePath))
        {
            // По имени файла exe - ловит случаи вроде «KeePass.exe» в произвольном каталоге
            var exeName = StripExtension(exePath);
            if (!string.IsNullOrEmpty(exeName) && state.ApplicationNames.Contains(exeName))
            {
                return true;
            }

            // По полному нормализованному пути exe
            if (state.ApplicationExePaths.Contains(NormalizePath(exePath)))
            {
                return true;
            }
        }

        return false;
    }

    public bool IsDirectoryExcluded(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var state = Volatile.Read(ref _current);
        var normalized = NormalizePath(path);
        foreach (var directory in state.Directories)
        {
            // Префикс с границей каталога: «C:\work» исключает «C:\work» и
            // «C:\work\...», но не «C:\workspace-secret»
            if (string.Equals(normalized, directory, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public bool IsNeverRecorded(Observable o)
    {
        var rules = Volatile.Read(ref _current).Rules;
        if (rules.Length == 0)
        {
            return false;
        }

        foreach (var rule in rules)
        {
            if (Matches(rule, o))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Правило §4.3: BY_APP_NAME, BY_HOST, BY_PATH_SUBSTR.</summary>
    private static bool Matches(NeverRecordRule rule, Observable o)
    {
        var value = rule.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (rule.Kind)
        {
            case NeverRecordRuleKind.ByAppName:
                // Имя приложения в наблюдаемом или имя exe (путь важнее имени)
                if (!string.IsNullOrWhiteSpace(o.ApplicationName)
                    && string.Equals(StripExtension(o.ApplicationName), StripExtension(value), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return !string.IsNullOrWhiteSpace(o.ApplicationExePath)
                       && string.Equals(StripExtension(o.ApplicationExePath), StripExtension(value), StringComparison.OrdinalIgnoreCase);

            case NeverRecordRuleKind.ByHost:
                // Только когда URL реально получен (§4.3): домен или его суффикс
                if (string.IsNullOrWhiteSpace(o.Value) || !Uri.TryCreate(o.Value, UriKind.Absolute, out var uri))
                {
                    return false;
                }

                var host = uri.Host;
                return string.Equals(host, value, StringComparison.OrdinalIgnoreCase)
                       || host.EndsWith("." + value.TrimStart('.'), StringComparison.OrdinalIgnoreCase);

            case NeverRecordRuleKind.ByPathSubstring:
                return !string.IsNullOrWhiteSpace(o.Value)
                       && o.Value.Contains(value, StringComparison.OrdinalIgnoreCase);

            default:
                return false;
        }
    }

    private static State BuildState(
        IEnumerable<string> applications,
        IEnumerable<string> directories,
        IEnumerable<NeverRecordRule> rules)
    {
        // Дефолты всегда на месте; пользовательские записи добавляются поверх
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var application in DefaultApplications)
        {
            AddApplication(names, exePaths, application);
        }

        foreach (var application in applications)
        {
            AddApplication(names, exePaths, application);
        }

        var dirs = new List<string>();
        void AddDir(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            dirs.Add(NormalizePath(path.Trim()));
        }

        // §4.2: системные хранилища секретов - исключены всегда, неудаляемо
        AddDir(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Credentials"));
        AddDir(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Credentials"));
        AddDir(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"));
        AddDir(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gnupg"));
        // Контейнеры UWP - «%APPDATA%\..\Local\Packages\*» из спецификации:
        // %LOCALAPPDATA% и есть %APPDATA%\..\Local, а префиксное сравнение
        // покрывает «\*» целиком
        AddDir(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages"));

        foreach (var directory in directories)
        {
            AddDir(directory);
        }

        var normalizedRules = rules
            .Where(r => !string.IsNullOrWhiteSpace(r.Value))
            .Select(r => r with { Value = r.Value.Trim() })
            .ToArray();

        return new State
        {
            ApplicationNames = names,
            ApplicationExePaths = exePaths,
            Directories = [.. dirs],
            Rules = normalizedRules,
        };
    }

    private static void AddApplication(HashSet<string> names, HashSet<string> exePaths, string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return;
        }

        var trimmed = entry.Trim();
        if (LooksLikePath(trimmed))
        {
            // Запись-путь сравнивается как полный нормализованный путь exe
            exePaths.Add(NormalizePath(trimmed));
        }
        else
        {
            // Запись-имя сравнивается как имя процесса без расширения
            names.Add(StripExtension(trimmed));
        }
    }

    private static bool LooksLikePath(string value) =>
        value.Contains('\\') || value.Contains('/') || value.Contains(':');

    private static string StripExtension(string nameOrPath) =>
        Path.GetFileNameWithoutExtension(nameOrPath.Trim());

    private static string NormalizePath(string path)
    {
        string fullPath;
        try
        {
            // Разрешает «..», относительные пути и разделители «/»
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Битый путь не должен ронять конвейер: сравниваем как есть
            fullPath = path;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
