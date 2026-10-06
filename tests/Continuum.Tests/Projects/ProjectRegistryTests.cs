using System;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Projects;
using Continuum.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Continuum.Tests.Projects;

public class ProjectRegistryTests
{
    private sealed class StubResolver(Func<string, string?> resolve) : IProjectResolver
    {
        public string? ResolveRoot(string path) => resolve(path);
    }

    private sealed class StubClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        public DateTimeOffset Now => UtcNow;
    }

    private static ProjectRegistry MakeRegistry(FakeRepository repository, Func<string, string?> resolve)
        => new(new StubResolver(resolve), repository, new StubClock(), NullLogger<ProjectRegistry>.Instance);

    [Fact]
    public async Task Path_outside_git_is_not_a_project()
    {
        var repository = new FakeRepository();
        var registry = MakeRegistry(repository, _ => null);

        var id = await registry.RegisterPathAsync("C:\\temp\\file.txt");

        Assert.Null(id);
        Assert.Empty(registry.RegisteredRoots);
        Assert.Empty(repository.Projects);
    }

    [Fact]
    public async Task First_registration_upserts_project_with_directory_name()
    {
        var repository = new FakeRepository();
        var registry = MakeRegistry(repository, _ => "C:\\work\\continuum");

        var id = await registry.RegisterPathAsync("C:\\work\\continuum\\readme.md");

        Assert.NotNull(id);
        var project = Assert.Single(repository.Projects);
        Assert.Equal("continuum", project.Name);
        Assert.Equal("C:\\work\\continuum", project.RootPath);
        Assert.Equal(id, registry.GetProjectId("C:\\work\\continuum"));
        Assert.Equal(["C:\\work\\continuum"], registry.RegisteredRoots);
    }

    [Fact]
    public async Task Same_root_registers_once_no_matter_how_many_paths()
    {
        var repository = new FakeRepository();
        var registry = MakeRegistry(repository, _ => "C:\\work\\continuum");

        var first = await registry.RegisterPathAsync("C:\\work\\continuum\\a.cs");
        var second = await registry.RegisterPathAsync("C:\\work\\continuum\\sub\\b.cs");
        var third = await registry.RegisterPathAsync("C:\\work\\continuum");

        Assert.Equal(first, second);
        Assert.Equal(first, third);
        Assert.Single(repository.Projects); // upsert один раз, не на каждый путь
    }

    [Fact]
    public async Task Resolver_failure_is_swallowed_and_returns_null()
    {
        var repository = new FakeRepository();
        var registry = MakeRegistry(repository, _ => throw new InvalidOperationException("io"));

        var id = await registry.RegisterPathAsync("C:\\work\\x");

        Assert.Null(id);
        Assert.Empty(repository.Projects);
    }

    [Fact]
    public async Task Empty_path_returns_null_without_resolver_call()
    {
        var repository = new FakeRepository();
        var called = false;
        var registry = MakeRegistry(repository, _ => { called = true; return "C:\\x"; });

        Assert.Null(await registry.RegisterPathAsync(""));
        Assert.Null(await registry.RegisterPathAsync("   "));
        Assert.False(called);
    }
}
