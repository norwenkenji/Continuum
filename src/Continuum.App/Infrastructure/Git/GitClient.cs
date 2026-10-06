using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Microsoft.Win32;

namespace Continuum.Infrastructure.Git;

/// <summary>
/// Единственный компонент системы, запускающий git.exe (data-sources §4).
/// Только читающие команды §4.2, всегда с «-c credential.helper=», окружение
/// §4.3, таймаут 5 с. Любая проблема (git не найден, таймаут, ненулевой код,
/// мусорный вывод) - GitStateInfo.Unavailable или null, НИКОГДА исключение.
/// </summary>
public sealed class GitClient : IGitClient
{
    /// <summary>Таймаут одного вызова git (§4.3).</summary>
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(5);

    // §4.3: окружение каждого вызова git - ни интерактива, ни опциональных блокировок
    private static readonly IReadOnlyDictionary<string, string> GitEnvironment =
        new Dictionary<string, string>
        {
            ["GIT_TERMINAL_PROMPT"] = "0", // запрет интерактивного запроса учётных данных
            ["GIT_OPTIONAL_LOCKS"] = "0",  // страховка на уровне окружения
            ["GCM_INTERACTIVE"] = "never", // Git Credential Manager без UI
            ["LC_ALL"] = "C",              // стабильный язык вывода
        };

    private readonly IProcessRunner _runner;
    private readonly string? _gitExeOverride;
    private readonly object _gitExeGate = new();
    private string? _gitExe;
    private bool _gitExeSearched;

    /// <param name="gitExePath">
    /// Явный путь к git.exe (шов для тестов и отладки): null - поиск по §4.1,
    /// пустая строка - принудительное «git не найден».
    /// </param>
    public GitClient(IProcessRunner runner, string? gitExePath = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
        _gitExeOverride = gitExePath;
    }

    /// <inheritdoc/>
    public string? FindGitExe()
    {
        lock (_gitExeGate)
        {
            if (_gitExeOverride is not null)
            {
                return _gitExeOverride.Length == 0 ? null : _gitExeOverride;
            }

            if (!_gitExeSearched)
            {
                _gitExe = SearchGitExe();
                _gitExeSearched = true; // ищем один раз за жизнь процесса
            }

            return _gitExe;
        }
    }

    /// <inheritdoc/>
    public async Task<string?> GetRepositoryRootAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var git = FindGitExe();
        if (git is null)
        {
            return null;
        }

        // cwd - каталог пути: для существующего файла - его каталог, иначе сам путь
        var workingDirectory = GetWorkingDirectory(path);
        if (workingDirectory is null)
        {
            return null;
        }

        var result = await RunGitAsync(git, workingDirectory, ct, "rev-parse", "--show-toplevel").ConfigureAwait(false);
        if (result is null || result.ExitCode != 0)
        {
            return null;
        }

        var root = FirstLine(result.StdOut);
        if (root.Length == 0)
        {
            return null;
        }

        try
        {
            return NormalizeGitPath(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null; // мусорный вывод - «не репозиторий»
        }
    }

    /// <inheritdoc/>
    public async Task<GitStateInfo> GetStateAsync(string repositoryRoot, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(repositoryRoot))
            {
                return GitStateInfo.Unavailable;
            }

            var git = FindGitExe();
            if (git is null)
            {
                return GitStateInfo.Unavailable;
            }

            // Ветка: «HEAD» - detached, активной ветки нет
            var branchResult = await RunGitAsync(git, repositoryRoot, ct, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
            if (branchResult is null)
            {
                return GitStateInfo.Unavailable;
            }

            string? branch;
            if (branchResult.ExitCode == 0)
            {
                var branchLine = FirstLine(branchResult.StdOut);
                if (branchLine.Length == 0)
                {
                    return GitStateInfo.Unavailable;
                }

                branch = string.Equals(branchLine, "HEAD", StringComparison.Ordinal) ? null : branchLine;
            }
            else
            {
                // Осознанное расширение §4.2: на репозитории БЕЗ коммитов rev-parse
                // --abbrev-ref HEAD падает с кодом 128 и печатает «HEAD», то есть имя
                // ветки четырьмя разрешёнными командами не получить. Fallback -
                // «symbolic-ref --short HEAD»: строго читающая plumbing-команда, без
                // сети и мутаций. Тоже не сработала (не репозиторий) - Unavailable.
                var symbolicResult = await RunGitAsync(git, repositoryRoot, ct, "symbolic-ref", "--short", "HEAD").ConfigureAwait(false);
                if (symbolicResult is null || symbolicResult.ExitCode != 0)
                {
                    return GitStateInfo.Unavailable;
                }

                var symbolicLine = FirstLine(symbolicResult.StdOut);
                if (symbolicLine.Length == 0)
                {
                    return GitStateInfo.Unavailable;
                }

                branch = symbolicLine;
            }

            // HEAD: ненулевой код или пустой вывод - репозиторий без коммитов,
            // это штатное состояние, а не недоступность
            string? headCommit = null;
            string? headSubject = null;
            DateTimeOffset? headTimestamp = null;

            var logResult = await RunGitAsync(git, repositoryRoot, ct, "log", "-1", "--format=%H%x09%ct%x09%s").ConfigureAwait(false);
            if (logResult is null)
            {
                return GitStateInfo.Unavailable; // таймаут/сбой запуска - проблема
            }

            var logLine = FirstLine(logResult.StdOut);
            if (logResult.ExitCode == 0 && logLine.Length > 0
                && !TryParseHead(logLine, out headCommit, out headTimestamp, out headSubject))
            {
                return GitStateInfo.Unavailable; // мусорный вывод
            }

            // Dirty-файлы. «--no-optional-locks» - глобальная опция git, а не
            // флаг status (как флаг она не поддерживается, проверено на git 2.54:
            // exit 129), поэтому стоит до подкоманды, сразу после обязательного
            // «-c». «core.quotePath=false»: не-ASCII имена (кириллица) приходят
            // сырыми UTF-8, а не восьмеричными эскейпами - парсер их не разбирает
            var statusResult = await RunGitAsync(git, repositoryRoot, ct,
                "-c", "core.quotePath=false", "--no-optional-locks", "status", "--porcelain").ConfigureAwait(false);
            if (statusResult is null || statusResult.ExitCode != 0)
            {
                return GitStateInfo.Unavailable;
            }

            var dirtyFiles = PorcelainParser.Parse(statusResult.StdOut);

            return new GitStateInfo(true, branch, headCommit, headSubject, headTimestamp, dirtyFiles);
        }
        catch
        {
            return GitStateInfo.Unavailable; // «никогда не бросает»
        }
    }

    /// <summary>
    /// Каноническая форма пути из git: '/' → '\', полный путь (разрешает «..»),
    /// без завершающего разделителя, нижний регистр. Та же каноническая форма
    /// обязана быть в ProjectResolver.
    /// </summary>
    internal static string NormalizeGitPath(string path)
    {
        var fixedSlashes = path.Replace('/', '\\');
        var full = Path.GetFullPath(fixedSlashes);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
    }

    // Поиск git.exe по §4.1: первый существующий файл из списка кандидатов
    private static string? SearchGitExe()
    {
        foreach (var candidate in EnumerateCandidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        // 1. PATH
        var pathEnvironment = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnvironment))
        {
            foreach (var directory in pathEnvironment.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return Path.Combine(directory, "git.exe");
            }
        }

        // 2-3. Реестр GitForWindows: HKLM, затем HKCU (чтение HKLM админки не требует)
        foreach (var rootKey in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            string? installPath = null;
            try
            {
                using var key = rootKey.OpenSubKey(@"SOFTWARE\GitForWindows");
                installPath = key?.GetValue("InstallPath") as string;
            }
            catch
            {
                // ключа нет или он битый - идём дальше
            }

            if (!string.IsNullOrWhiteSpace(installPath))
            {
                yield return Path.Combine(installPath, "cmd", "git.exe");
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // 4. Пользовательская установка Git
        yield return Path.Combine(localAppData, "Programs", "Git", "cmd", "git.exe");

        // 5. Git в составе GitHub Desktop - свежайший каталог app-*
        var desktopRoot = Path.Combine(localAppData, "GitHubDesktop");
        string? latestApp = null;
        try
        {
            if (Directory.Exists(desktopRoot))
            {
                latestApp = Directory.EnumerateDirectories(desktopRoot, "app-*")
                    .OrderByDescending(Directory.GetLastWriteTimeUtc)
                    .ThenByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            }
        }
        catch
        {
            // доступа нет - идём дальше
        }

        if (latestApp is not null)
        {
            yield return Path.Combine(latestApp, "resources", "app", "git", "cmd", "git.exe");
        }

        // 6. Системная установка
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
        {
            yield return Path.Combine(programFiles, "Git", "cmd", "git.exe");
        }
    }

    /// <summary>
    /// Единственная точка запуска git: префикс «-c credential.helper=»,
    /// окружение §4.3, таймаут 5 с. null - таймаут или сбой запуска;
    /// ненулевой код выхода - данные в результате, решает вызывающий.
    /// </summary>
    private async Task<ProcessResult?> RunGitAsync(string gitExe, string workingDirectory, CancellationToken ct, params string[] arguments)
    {
        var fullArguments = new List<string>(arguments.Length + 2) { "-c", "credential.helper=" };
        fullArguments.AddRange(arguments);

        try
        {
            var result = await _runner.RunAsync(gitExe, fullArguments, workingDirectory, GitEnvironment, GitTimeout, ct).ConfigureAwait(false);
            return result.TimedOut ? null : result;
        }
        catch
        {
            // Процесс не запустился, внешняя отмена, гонка - всё это «git недоступен»
            return null;
        }
    }

    private static string? GetWorkingDirectory(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return Path.GetDirectoryName(Path.GetFullPath(path)) ?? path;
            }

            // Каталог или несуществующий путь - git сам ответит «не репозиторий»
            return path;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string FirstLine(string output)
    {
        var lineEnd = output.IndexOf('\n');
        var line = lineEnd >= 0 ? output[..lineEnd] : output;
        return line.Trim();
    }

    /// <summary>Разбор строки «hash{TAB}unixtime{TAB}subject» из git log (subject может содержать TAB).</summary>
    private static bool TryParseHead(string line, out string? commit, out DateTimeOffset? timestamp, out string? subject)
    {
        commit = null;
        timestamp = null;
        subject = null;

        var firstTab = line.IndexOf('\t');
        if (firstTab <= 0)
        {
            return false;
        }

        var secondTab = line.IndexOf('\t', firstTab + 1);
        if (secondTab <= firstTab + 1)
        {
            return false;
        }

        if (!long.TryParse(
                line[(firstTab + 1)..secondTab],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var unixSeconds))
        {
            return false;
        }

        commit = line[..firstTab];
        timestamp = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        subject = line[(secondTab + 1)..];
        return true;
    }
}
