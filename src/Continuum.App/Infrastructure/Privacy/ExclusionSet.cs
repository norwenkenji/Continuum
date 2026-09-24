using System;
using System.Collections.Generic;
using System.IO;
using Continuum.Core.Abstractions;

namespace Continuum.Infrastructure.Privacy;

/// <summary>
/// Набор исключений privacy-конвейера: приложения и директории.
/// Спецификация: docs/specs/privacy-pipeline.md, §4.
/// Дефолты (менеджеры паролей, собственное приложение, системные хранилища
/// секретов) неудаляемы: API удаления в классе просто нет.
/// Все сравнения регистронезависимые.
/// </summary>
public sealed class ExclusionSet : IExclusionSet
{
    // §4.1: менеджеры паролей + собственное приложение — не наблюдаются никогда
    private static readonly string[] DefaultApplications =
    [
        "1password", "keepass", "bitwarden", "enpass", "dashlane",
        "lastpass", "nordpass", "stickypasswords", "keeper",
        "continuum", // собственное окно исключается безусловно
    ];

    // Имена процессов без расширения
    private readonly HashSet<string> _applicationNames = new(StringComparer.OrdinalIgnoreCase);

    // Полные нормализованные пути exe — «путь важнее имени» (§4.1)
    private readonly HashSet<string> _applicationExePaths = new(StringComparer.OrdinalIgnoreCase);

    // Нормализованные директории без завершающего разделителя
    private readonly List<string> _excludedDirectories = [];

    public ExclusionSet(IEnumerable<string>? extraApplications = null, IEnumerable<string>? extraDirectories = null)
    {
        foreach (var application in DefaultApplications)
        {
            AddApplication(application);
        }

        // §4.2: системные хранилища секретов — исключены всегда, неудаляемо
        AddDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Credentials"));
        AddDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Credentials"));
        AddDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"));
        AddDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gnupg"));

        if (extraApplications is not null)
        {
            foreach (var application in extraApplications)
            {
                AddApplication(application);
            }
        }

        if (extraDirectories is not null)
        {
            foreach (var directory in extraDirectories)
            {
                AddDirectory(directory);
            }
        }
    }

    public bool IsApplicationExcluded(string applicationName, string? exePath)
    {
        // По нормализованному имени процесса (без учёта регистра, без расширения)
        if (!string.IsNullOrWhiteSpace(applicationName)
            && _applicationNames.Contains(StripExtension(applicationName)))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(exePath))
        {
            // По имени файла exe — ловит случаи вроде «KeePass.exe» в произвольном каталоге
            var exeName = StripExtension(exePath);
            if (!string.IsNullOrEmpty(exeName) && _applicationNames.Contains(exeName))
            {
                return true;
            }

            // По полному нормализованному пути exe
            if (_applicationExePaths.Contains(NormalizePath(exePath)))
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

        var normalized = NormalizePath(path);
        foreach (var directory in _excludedDirectories)
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
        // Правила §4.3 (BY_APP_NAME / BY_HOST / BY_PATH_SUBSTR) появятся вместе
        // с окном настроек (шаг 9 в readme). Контракт уже подключён в
        // PrivacyFilter, но список правил пока пуст — ничего не запрещаем.
        return false;
    }

    private void AddApplication(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return;
        }

        var trimmed = entry.Trim();
        if (LooksLikePath(trimmed))
        {
            // Запись-путь сравнивается как полный нормализованный путь exe
            _applicationExePaths.Add(NormalizePath(trimmed));
        }
        else
        {
            // Запись-имя сравнивается как имя процесса без расширения
            _applicationNames.Add(StripExtension(trimmed));
        }
    }

    private void AddDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _excludedDirectories.Add(NormalizePath(path.Trim()));
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
