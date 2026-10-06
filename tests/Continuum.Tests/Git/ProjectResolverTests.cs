using System;
using System.IO;
using Continuum.Infrastructure.Git;
using Xunit;

namespace Continuum.Tests.Git;

/// <summary>
/// ProjectResolver на реальных временных каталогах в %TEMP%: подъём по
/// маркеру «.git», вложенные репозитории, worktree-маркер, нормализация.
/// Тест subst-диска не написан: subst нельзя создать внутри процесса теста
/// без внешней команды - ограничение зафиксировано в отчёте.
/// </summary>
public class ProjectResolverTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "continuum-resolver-tests", Guid.NewGuid().ToString("N"));
    private readonly ProjectResolver _resolver = new();

    public ProjectResolverTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void File_in_subdirectory_resolves_to_repo_root()
    {
        var repo = Path.Combine(_tempRoot, "Repo");
        var sub = Path.Combine(repo, "src", "deep");
        Directory.CreateDirectory(sub);
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var file = Path.Combine(sub, "a.txt");
        File.WriteAllText(file, "x");

        Assert.Equal(Canonical(repo), _resolver.ResolveRoot(file));
    }

    [Fact]
    public void Directory_with_marker_resolves_to_itself()
    {
        var repo = Path.Combine(_tempRoot, "PlainRepo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        Assert.Equal(Canonical(repo), _resolver.ResolveRoot(repo));
    }

    [Fact]
    public void Nested_repository_resolves_to_outermost_root()
    {
        var outer = Path.Combine(_tempRoot, "Outer");
        var inner = Path.Combine(outer, "Nested");
        Directory.CreateDirectory(inner);
        Directory.CreateDirectory(Path.Combine(outer, ".git"));
        Directory.CreateDirectory(Path.Combine(inner, ".git")); // вложенный репозиторий

        // Внешний корень важнее: вложенные репозитории - один проект
        Assert.Equal(Canonical(outer), _resolver.ResolveRoot(inner));
    }

    [Fact]
    public void Git_file_counts_as_worktree_marker()
    {
        var repo = Path.Combine(_tempRoot, "WorktreeRepo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, ".git"), "gitdir: C:/elsewhere/main/.git/worktrees/wt");

        Assert.Equal(Canonical(repo), _resolver.ResolveRoot(repo));
    }

    [Fact]
    public void Directory_outside_repository_gives_null()
    {
        var plain = Path.Combine(_tempRoot, "Plain");
        Directory.CreateDirectory(plain);

        Assert.Null(_resolver.ResolveRoot(plain));
    }

    [Fact]
    public void Result_is_normalized_trailing_separator_dots_and_case()
    {
        var repo = Path.Combine(_tempRoot, "CaseRepo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var sub = Path.Combine(repo, "Sub");
        Directory.CreateDirectory(sub);

        // Указывает на repo через «..», в верхнем регистре, с завершающим разделителем
        var messy = sub.ToUpperInvariant()
            + Path.DirectorySeparatorChar + ".."
            + Path.DirectorySeparatorChar;

        var resolved = _resolver.ResolveRoot(messy);

        Assert.Equal(Canonical(repo), resolved);
        Assert.Equal(resolved, resolved!.ToLowerInvariant());
        Assert.False(resolved.EndsWith(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Nonexistent_or_broken_path_gives_null()
    {
        Assert.Null(_resolver.ResolveRoot(Path.Combine(_tempRoot, "no-such-dir")));
        Assert.Null(_resolver.ResolveRoot("   "));
        Assert.Null(_resolver.ResolveRoot(":::not a path:::"));
    }

    private static string Canonical(string path) =>
        Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant();

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
