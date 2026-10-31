using System;
using System.Collections.Generic;
using System.IO;
using Continuum.Restore;
using Xunit;

namespace Continuum.Tests.Restore;

/// <summary>
/// ApplicationPathResolver на стабе IAppPathSource: порядок источников,
/// пропуск битых значений, кэш положительных и отрицательных результатов,
/// Uninstall-матч и PATH-перебор. Реестр в тестах не трогается.
/// Имена уникальны («ztestapp» и т.п.), чтобы реальные известные
/// расположения машины не влияли на результат.
/// </summary>
public class ApplicationPathResolverTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "continuum-resolver-tests", Guid.NewGuid().ToString("N"));

    public ApplicationPathResolverTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void App_paths_value_is_returned_and_uninstall_is_not_queried()
    {
        var source = new StubAppPathSource
        {
            AppPathEntries = ["C:\\apps\\code.exe"],
        };
        var resolver = new ApplicationPathResolver(source);

        var result = resolver.ResolveExecutable("code");

        Assert.Equal("C:\\apps\\code.exe", result);
        Assert.Equal(1, source.AppPathCalls);
        Assert.Equal(0, source.UninstallCalls); // дорогой поиск не нужен
    }

    [Fact]
    public void Broken_app_paths_values_are_skipped_until_absolute_one()
    {
        // Кандидаты идут в порядке приоритета (у реестра: HKCU → HKLM):
        // первые два битые - берём первый абсолютный
        var source = new StubAppPathSource
        {
            AppPathEntries = ["code.exe", "  ", "C:\\hkcu\\code.exe", "C:\\hklm\\code.exe"],
        };
        var resolver = new ApplicationPathResolver(source);

        Assert.Equal("C:\\hkcu\\code.exe", resolver.ResolveExecutable("code"));
    }

    [Fact]
    public void Quoted_app_paths_value_is_unquoted()
    {
        var source = new StubAppPathSource
        {
            AppPathEntries = ["\"C:\\quoted\\code.exe\""],
        };
        var resolver = new ApplicationPathResolver(source);

        Assert.Equal("C:\\quoted\\code.exe", resolver.ResolveExecutable("code"));
    }

    [Fact]
    public void Nothing_found_gives_null()
    {
        var resolver = new ApplicationPathResolver(new StubAppPathSource());

        Assert.Null(resolver.ResolveExecutable("znothingapp"));
    }

    [Fact]
    public void Positive_result_is_cached()
    {
        var source = new StubAppPathSource
        {
            AppPathEntries = ["C:\\apps\\code.exe"],
        };
        var resolver = new ApplicationPathResolver(source);

        Assert.Equal("C:\\apps\\code.exe", resolver.ResolveExecutable("code"));
        Assert.Equal("C:\\apps\\code.exe", resolver.ResolveExecutable("code"));
        Assert.Equal(1, source.AppPathCalls); // второй вызов не ходит в источник
    }

    [Fact]
    public void Negative_result_is_cached_too()
    {
        var source = new StubAppPathSource();
        var resolver = new ApplicationPathResolver(source);

        Assert.Null(resolver.ResolveExecutable("znothingapp"));
        Assert.Null(resolver.ResolveExecutable("znothingapp"));
        Assert.Equal(1, source.AppPathCalls);
        Assert.Equal(1, source.UninstallCalls); // только первый вызов дошёл до источников
        Assert.Equal(1, source.PathCalls);
    }

    [Fact]
    public void Exe_suffix_is_normalized_and_shared_cache_entry()
    {
        var source = new StubAppPathSource
        {
            AppPathEntries = ["C:\\apps\\code.exe"],
        };
        var resolver = new ApplicationPathResolver(source);

        Assert.Equal("C:\\apps\\code.exe", resolver.ResolveExecutable("code"));
        Assert.Equal("C:\\apps\\code.exe", resolver.ResolveExecutable("code.exe"));
        Assert.Equal(1, source.AppPathCalls); // «code» и «code.exe» - один ключ кэша
    }

    [Fact]
    public void Uninstall_match_by_display_name_gives_exe_from_install_location()
    {
        var installLocation = CreateDirectory("uninstall-direct");
        var exe = Path.Combine(installLocation, "ztestapp.exe");
        File.WriteAllText(exe, "stub");
        var source = new StubAppPathSource
        {
            UninstallEntries =
            [
                new UninstallEntry("Другое приложение", CreateDirectory("uninstall-other")),
                new UninstallEntry("ZTestApp (установка)", installLocation + Path.DirectorySeparatorChar),
            ],
        };
        var resolver = new ApplicationPathResolver(source);

        Assert.Equal(exe, resolver.ResolveExecutable("ztestapp"));
    }

    [Fact]
    public void Uninstall_match_falls_back_to_bin_subdirectory()
    {
        var installLocation = CreateDirectory("uninstall-bin");
        var binExe = Path.Combine(installLocation, "bin", "zbinapp.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(binExe)!);
        File.WriteAllText(binExe, "stub");
        var source = new StubAppPathSource
        {
            UninstallEntries = [new UninstallEntry("ZBinApp", installLocation)],
        };
        var resolver = new ApplicationPathResolver(source);

        Assert.Equal(binExe, resolver.ResolveExecutable("zbinapp"));
    }

    [Fact]
    public void Uninstall_entry_without_install_location_is_skipped()
    {
        var source = new StubAppPathSource
        {
            UninstallEntries = [new UninstallEntry("ZTestApp", null)],
        };
        var resolver = new ApplicationPathResolver(source);

        Assert.Null(resolver.ResolveExecutable("ztestapp"));
    }

    [Fact]
    public void Path_directories_are_searched_last()
    {
        var first = CreateDirectory("path-first");
        var second = CreateDirectory("path-second");
        var exe = Path.Combine(second, "zpathapp.exe");
        File.WriteAllText(exe, "stub");
        var source = new StubAppPathSource
        {
            PathDirectories = [first, second],
        };
        var resolver = new ApplicationPathResolver(source);

        Assert.Equal(exe, resolver.ResolveExecutable("zpathapp"));
        Assert.Equal(1, source.PathCalls);
    }

    [Fact]
    public void Null_or_empty_name_gives_null_without_sources()
    {
        var resolver = new ApplicationPathResolver(new StubAppPathSource());

        Assert.Null(resolver.ResolveExecutable(null!));
        Assert.Null(resolver.ResolveExecutable("   "));
        Assert.Null(resolver.ResolveExecutable(".exe"));
    }

    private string CreateDirectory(string name)
    {
        var path = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    /// <summary>Стаб источников путей: фиксированные списки + счётчики вызовов.</summary>
    private sealed class StubAppPathSource : IAppPathSource
    {
        public List<string> AppPathEntries { get; init; } = [];

        public List<UninstallEntry> UninstallEntries { get; init; } = [];

        public List<string> PathDirectories { get; init; } = [];

        public int AppPathCalls { get; private set; }

        public int UninstallCalls { get; private set; }

        public int PathCalls { get; private set; }

        public IReadOnlyList<string> GetAppPathEntries(string applicationName)
        {
            AppPathCalls++;
            return AppPathEntries;
        }

        public IReadOnlyList<UninstallEntry> GetUninstallEntries()
        {
            UninstallCalls++;
            return UninstallEntries;
        }

        public IReadOnlyList<string> GetPathDirectories()
        {
            PathCalls++;
            return PathDirectories;
        }
    }
}
