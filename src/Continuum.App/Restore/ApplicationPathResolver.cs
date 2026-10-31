using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Continuum.Core.Abstractions;
using Microsoft.Win32;

namespace Continuum.Restore;

/// <summary>
/// Шов внешних источников путей для ApplicationPathResolver (App-уровень,
/// НЕ Core): реестр и переменная PATH. Реальные источники дёрговаты и
/// машинозависимы - в тестах подменяются стабом. Public, потому что
/// тестовая сборка отдельная (InternalsVisibleTo в проекте нет).
/// </summary>
public interface IAppPathSource
{
    /// <summary>
    /// Кандидаты из раздела App Paths в порядке приоритета (HKCU, HKLM,
    /// Wow6432Node). Значения НЕ валидированы: проверку абсолютности пути
    /// делает резолвер - битые значения пропускаются.
    /// </summary>
    IReadOnlyList<string> GetAppPathEntries(string applicationName);

    /// <summary>Записи раздела Uninstall: отображаемое имя и каталог установки.</summary>
    IReadOnlyList<UninstallEntry> GetUninstallEntries();

    /// <summary>Каталоги из PATH для перебора.</summary>
    IReadOnlyList<string> GetPathDirectories();
}

/// <summary>Запись раздела Uninstall - то, что нужно резолверу.</summary>
public sealed record UninstallEntry(string DisplayName, string? InstallLocation);

/// <summary>
/// Резолвер путей приложений (readme, шаг 6): App Paths → Uninstall →
/// известные расположения → PATH. Нужен, чтобы план восстановления не
/// падал на машине, где code/wt/chrome не в PATH. Результат (в том числе
/// отрицательный - null) кэшируется на время жизни процесса. Никогда не
/// бросает: любая ошибка источника - переход к следующему.
/// </summary>
public sealed class ApplicationPathResolver : IApplicationResolver
{
    private readonly IAppPathSource _source;

    // Кэш по нормализованному имени; null кэшируется тоже (отрицательный результат)
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ApplicationPathResolver(IAppPathSource? source = null)
    {
        _source = source ?? new RegistryAppPathSource();
    }

    /// <inheritdoc/>
    public string? ResolveExecutable(string applicationName)
    {
        if (string.IsNullOrWhiteSpace(applicationName))
        {
            return null;
        }

        var baseName = NormalizeBaseName(applicationName);
        if (baseName.Length == 0)
        {
            return null;
        }

        return _cache.GetOrAdd(baseName, ResolveUncached);
    }

    private string? ResolveUncached(string baseName)
    {
        // 1. App Paths - значения реестра доверенные: проверяем только
        // абсолютность пути; существование файла проверять не нужно
        // (устаревшая запись честно упадёт при запуске с внятной причиной)
        foreach (var candidate in _source.GetAppPathEntries(baseName))
        {
            var trimmed = candidate.Trim().Trim('"');
            if (IsAbsoluteExecutablePath(trimmed))
            {
                return trimmed;
            }
        }

        // 2. Uninstall - дорогой поиск, только если App Paths не дал результата
        var uninstallMatch = TryResolveFromUninstall(baseName);
        if (uninstallMatch is not null)
        {
            return uninstallMatch;
        }

        // 3. Известные расположения (VS Code/Chrome user-install и подобные)
        var knownMatch = TryResolveFromKnownLocations(baseName);
        if (knownMatch is not null)
        {
            return knownMatch;
        }

        // 4. PATH
        return TryResolveFromPath(baseName);
    }

    /// <summary>Записи Uninstall: DisplayName содержит имя → InstallLocation + типовые имена exe.</summary>
    private string? TryResolveFromUninstall(string baseName)
    {
        foreach (var entry in _source.GetUninstallEntries())
        {
            if (string.IsNullOrWhiteSpace(entry.InstallLocation) || string.IsNullOrEmpty(entry.DisplayName))
            {
                continue;
            }

            if (entry.DisplayName.IndexOf(baseName, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            var installLocation = entry.InstallLocation.Trim().Trim('"');
            var candidates = new[]
            {
                Path.Combine(installLocation, baseName + ".exe"),
                Path.Combine(installLocation, "bin", baseName + ".exe"),
            };
            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Известные расположения: %LOCALAPPDATA%\Programs, Program Files, Program Files (x86), GitHub Desktop.</summary>
    private static string? TryResolveFromKnownLocations(string baseName)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        // Пользовательская установка (VS Code, Chrome) - %LOCALAPPDATA%\Programs\<name>\<name>.exe
        var localPrograms = Path.Combine(localAppData, "Programs");
        var localCandidate = Path.Combine(localPrograms, baseName, baseName + ".exe");
        if (File.Exists(localCandidate))
        {
            return localCandidate;
        }

        // GitHub Desktop: app-*\GitHubDesktop.exe, свежайший каталог app-*
        if (string.Equals(baseName, "githubdesktop", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var desktopRoot = Path.Combine(localAppData, "GitHubDesktop");
                if (Directory.Exists(desktopRoot))
                {
                    var latestApp = Directory.EnumerateDirectories(desktopRoot, "app-*")
                        .OrderByDescending(Directory.GetLastWriteTimeUtc)
                        .ThenByDescending(directory => directory, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
                    if (latestApp is not null)
                    {
                        var desktopExe = Path.Combine(latestApp, "GitHubDesktop.exe");
                        if (File.Exists(desktopExe))
                        {
                            return desktopExe;
                        }
                    }
                }
            }
            catch
            {
                // перебор каталогов не удался - идём дальше
            }
        }

        var programFilesCandidate = Path.Combine(programFiles, baseName, baseName + ".exe");
        if (File.Exists(programFilesCandidate))
        {
            return programFilesCandidate;
        }

        if (!string.IsNullOrEmpty(programFilesX86))
        {
            var programFilesX86Candidate = Path.Combine(programFilesX86, baseName, baseName + ".exe");
            if (File.Exists(programFilesX86Candidate))
            {
                return programFilesX86Candidate;
            }
        }

        return null;
    }

    private string? TryResolveFromPath(string baseName)
    {
        foreach (var directory in _source.GetPathDirectories())
        {
            var candidate = Path.Combine(directory, baseName + ".exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>«code.exe» → «code»; трим по краям.</summary>
    private static string NormalizeBaseName(string applicationName)
    {
        var baseName = applicationName.Trim();
        if (baseName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            baseName = baseName[..^4];
        }

        return baseName;
    }

    private static bool IsAbsoluteExecutablePath(string path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path);

    /// <summary>Реальные источники: реестр (App Paths, Uninstall) и PATH.</summary>
    private sealed class RegistryAppPathSource : IAppPathSource
    {
        private const string AppPathsRoot = @"Software\Microsoft\Windows\CurrentVersion\App Paths";
        private const string UninstallRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        private const string Wow6432NodePrefix = @"Software\WOW6432Node\";

        public IReadOnlyList<string> GetAppPathEntries(string applicationName)
        {
            var entries = new List<string>();

            // Ключ App Paths называется «code.exe»; пробуем и короткое имя
            var keyNames = new List<string> { applicationName };
            if (applicationName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                keyNames.Add(applicationName[..^4]);
            }
            else
            {
                keyNames.Add(applicationName + ".exe");
            }

            foreach (var (root, subKeyRoot) in AppPathsLocations())
            {
                foreach (var keyName in keyNames)
                {
                    try
                    {
                        using var key = root.OpenSubKey(subKeyRoot + "\\" + keyName);
                        // Значение по умолчанию (Default) ключа - путь к exe
                        if (key?.GetValue(null) is string value && !string.IsNullOrWhiteSpace(value))
                        {
                            entries.Add(value);
                        }
                    }
                    catch
                    {
                        // ключ недоступен - следующий кандидат
                    }
                }
            }

            return entries;
        }

        public IReadOnlyList<UninstallEntry> GetUninstallEntries()
        {
            var entries = new List<UninstallEntry>();
            foreach (var (root, subKeyRoot) in UninstallLocations())
            {
                try
                {
                    using var uninstallKey = root.OpenSubKey(subKeyRoot);
                    if (uninstallKey is null)
                    {
                        continue;
                    }

                    foreach (var entryKeyName in uninstallKey.GetSubKeyNames())
                    {
                        try
                        {
                            using var entryKey = uninstallKey.OpenSubKey(entryKeyName);
                            var displayName = entryKey?.GetValue("DisplayName") as string;
                            var installLocation = entryKey?.GetValue("InstallLocation") as string;
                            if (!string.IsNullOrEmpty(displayName))
                            {
                                entries.Add(new UninstallEntry(displayName, installLocation));
                            }
                        }
                        catch
                        {
                            // битая запись реестра - следующая
                        }
                    }
                }
                catch
                {
                    // раздел недоступен - следующий источник
                }
            }

            return entries;
        }

        public IReadOnlyList<string> GetPathDirectories()
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path))
            {
                return [];
            }

            return path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        // Порядок значим: HKCU раньше HKLM, Wow6432Node - последним (только 64-битные машины)
        private static IEnumerable<(RegistryKey Root, string SubKeyRoot)> AppPathsLocations()
        {
            yield return (Registry.CurrentUser, AppPathsRoot);
            yield return (Registry.LocalMachine, AppPathsRoot);
            if (Environment.Is64BitOperatingSystem)
            {
                yield return (Registry.LocalMachine, Wow6432NodePrefix + AppPathsRoot);
            }
        }

        private static IEnumerable<(RegistryKey Root, string SubKeyRoot)> UninstallLocations()
        {
            yield return (Registry.CurrentUser, UninstallRoot);
            yield return (Registry.LocalMachine, UninstallRoot);
            if (Environment.Is64BitOperatingSystem)
            {
                yield return (Registry.LocalMachine, Wow6432NodePrefix + UninstallRoot);
            }
        }
    }
}
