using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Runtime;
using Continuum.Snapshots;
using Continuum.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Continuum.Tests.Snapshots;

public class SnapshotterTests
{
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class StubSession(long id) : ISessionContext
    {
        public long CurrentSessionId => id;
    }

    private sealed class StubRegistry : IProjectRegistry
    {
        private readonly List<string> _roots = [];
        private readonly Dictionary<string, long> _ids = [];
        public IReadOnlyCollection<string> RegisteredRoots => _roots;
        public void Add(string root, long projectId) { _roots.Add(root); _ids[root] = projectId; }
        public void AddRootOnly(string root) => _roots.Add(root); // корень есть, upsert ещё нет
        public Task<long?> RegisterPathAsync(string path, CancellationToken ct = default) => Task.FromResult<long?>(null);
        public long? GetProjectId(string normalizedRoot) => _ids.TryGetValue(normalizedRoot, out var id) ? id : null;
    }

    private sealed class StubGit : IGitClient
    {
        private readonly Dictionary<string, GitStateInfo> _byRoot = [];
        public bool ThrowOnRoot { get; set; }
        public string? FindGitExe() => "git";
        public Task<string?> GetRepositoryRootAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public void SetState(string root, GitStateInfo state) => _byRoot[root] = state;

        public Task<GitStateInfo> GetStateAsync(string repositoryRoot, CancellationToken ct = default)
        {
            if (ThrowOnRoot)
            {
                throw new InvalidOperationException("сбой опроса");
            }

            return Task.FromResult(_byRoot.TryGetValue(repositoryRoot, out var s) ? s : GitStateInfo.Unavailable);
        }
    }

    private sealed class StubPrivacy : IPrivacyFilter
    {
        public Func<Observable, bool> Rule { get; set; } = _ => true;
        public bool Allow(Observable o) => Rule(o);
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static (Snapshotter, FakeRepository, StubRegistry, StubGit, StubPrivacy) Create(long sessionId = 7)
    {
        var repo = new FakeRepository();
        var registry = new StubRegistry();
        var git = new StubGit();
        var privacy = new StubPrivacy();
        var snapshotter = new Snapshotter(
            repo, new StubSession(sessionId), registry, git, privacy, new FixedClock(Now),
            NullLogger<Snapshotter>.Instance);
        return (snapshotter, repo, registry, git, privacy);
    }

    [Fact]
    public async Task Snapshot_without_session_is_skipped()
    {
        var (snapshotter, repo, registry, _, _) = Create(sessionId: 0);
        registry.Add("c:/repo", 1);

        var id = await snapshotter.SaveSnapshotAsync(SnapshotReason.Timer);

        Assert.Equal(0, id);
        Assert.Empty(repo.Snapshots);
    }

    [Fact]
    public async Task Snapshot_writes_git_state_per_registered_project()
    {
        var (snapshotter, repo, registry, git, _) = Create();
        registry.Add("c:/repo", 42);
        git.SetState("c:/repo", new GitStateInfo(
            true, "main", "a94a8fe", "feat: работа", Now.AddHours(-1),
            [new("src/a.cs", FileChangeKind.Modified), new("new.txt", FileChangeKind.Untracked)]));

        var id = await snapshotter.SaveSnapshotAsync(SnapshotReason.Timer);

        var snapshot = Assert.Single(repo.Snapshots);
        Assert.Equal(id, snapshot.Id);
        Assert.Equal(7, snapshot.SessionId);
        Assert.Equal(SnapshotReason.Timer, snapshot.Reason);
        Assert.Equal(Now, snapshot.Ts);

        var state = Assert.Single(snapshot.GitStates);
        Assert.Equal(42, state.ProjectId);
        Assert.True(state.Available);
        Assert.Equal("main", state.Branch);
        Assert.Equal("a94a8fe", state.HeadCommit);
        Assert.Equal("feat: работа", state.HeadSubject);
        Assert.Equal(Now.AddHours(-1), state.HeadTs);
        Assert.Equal(1, state.DirtyCount);      // modified
        Assert.Equal(1, state.UntrackedCount);  // untracked не считается dirty
        Assert.Contains("src/a.cs", state.DirtyFilesJson);
        Assert.Contains("new.txt", state.DirtyFilesJson);
    }

    [Fact]
    public async Task Unavailable_git_writes_available_zero_without_exception()
    {
        var (snapshotter, repo, registry, _, _) = Create();
        registry.Add("c:/not-a-repo", 5); // git вернёт Unavailable

        var id = await snapshotter.SaveSnapshotAsync(SnapshotReason.Shutdown);

        Assert.True(id > 0);
        var state = Assert.Single(Assert.Single(repo.Snapshots).GitStates);
        Assert.False(state.Available);
        Assert.Null(state.Branch);
        Assert.Equal(0, state.DirtyCount);
        Assert.Null(state.DirtyFilesJson);
    }

    [Fact]
    public async Task Failing_project_does_not_cancel_others()
    {
        var (snapshotter, repo, registry, git, _) = Create();
        registry.Add("c:/broken", 1);
        registry.Add("c:/ok", 2);
        git.SetState("c:/ok", new GitStateInfo(true, "dev", null, null, null, []));
        git.ThrowOnRoot = true; // бросает на всех корнях

        // Сбой опроса превращается в строку available=0, а не в отмену снапшота
        var id = await snapshotter.SaveSnapshotAsync(SnapshotReason.Timer);

        Assert.True(id > 0);
        var snapshot = Assert.Single(repo.Snapshots);
        Assert.Equal(2, snapshot.GitStates.Count);
        Assert.All(snapshot.GitStates, s => Assert.False(s.Available));
    }

    [Fact]
    public async Task Privacy_filter_drops_dirty_paths_from_git_state()
    {
        var (snapshotter, repo, registry, git, privacy) = Create();
        registry.Add("c:/repo", 9);
        git.SetState("c:/repo", new GitStateInfo(true, "main", null, null, null,
            [new("src/ok.cs", FileChangeKind.Modified), new("c:/secrets/key.pem", FileChangeKind.Modified)]));
        privacy.Rule = o => !o.Value!.Contains("key.pem");

        await snapshotter.SaveSnapshotAsync(SnapshotReason.Timer);

        var state = Assert.Single(Assert.Single(repo.Snapshots).GitStates);
        Assert.Equal(1, state.DirtyCount);
        Assert.Contains("src/ok.cs", state.DirtyFilesJson);
        Assert.DoesNotContain("key.pem", state.DirtyFilesJson);
    }

    [Fact]
    public async Task Root_without_project_id_is_skipped()
    {
        var (snapshotter, repo, registry, _, _) = Create();
        registry.AddRootOnly("c:/ghost"); // корень в списке, но проект не upsert-нут

        var id = await snapshotter.SaveSnapshotAsync(SnapshotReason.Timer);

        Assert.True(id > 0);
        Assert.Empty(Assert.Single(repo.Snapshots).GitStates);
    }
}
