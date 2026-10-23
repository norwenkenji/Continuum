using System;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Stopped;
using Continuum.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Continuum.Tests.Stopped;

/// <summary>
/// Таблица истинности движка «Где я остановился?» целиком (readme):
/// все ветки Unclear / MaybeUnfinished / Completed на фиксированных входных.
/// </summary>
public class StoppedEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private const string HeadHash = "abc1234567890abc1234567890abc1234567890ab";

    [Fact]
    public async Task No_sessions_gives_unclear_with_empty_facts()
    {
        var engine = CreateEngine(new FakeRepository(), new StubGitClient());

        var verdict = await engine.EvaluateAsync();

        AssertUnclearEmpty(verdict);
    }

    [Fact]
    public async Task Session_without_events_gives_unclear()
    {
        var repository = new FakeRepository();
        repository.Sessions.Add(MakeSession(1));
        var engine = CreateEngine(repository, new StubGitClient());

        var verdict = await engine.EvaluateAsync();

        AssertUnclearEmpty(verdict);
    }

    [Fact]
    public async Task Session_without_project_events_gives_unclear()
    {
        var repository = new FakeRepository();
        repository.Sessions.Add(MakeSession(1));
        repository.Events.Add(MakeEvent(1, 1, EventKind.AppFocused, projectId: null));
        var engine = CreateEngine(repository, new StubGitClient());

        var verdict = await engine.EvaluateAsync();

        AssertUnclearEmpty(verdict);
    }

    [Fact]
    public async Task Missing_project_record_gives_unclear()
    {
        var repository = new FakeRepository();
        repository.Sessions.Add(MakeSession(1));
        repository.Events.Add(MakeEvent(1, 1, EventKind.FileChanged, projectId: 42));
        // Проекта с id=42 в БД нет
        var engine = CreateEngine(repository, new StubGitClient());

        var verdict = await engine.EvaluateAsync();

        AssertUnclearEmpty(verdict);
    }

    [Fact]
    public async Task Unavailable_git_gives_unclear_but_project_facts_filled()
    {
        var repository = SeedProjectScenario(out var projectId, EventKind.GitCommit);
        var engine = CreateEngine(repository, new StubGitClient()); // Unavailable по умолчанию

        var verdict = await engine.EvaluateAsync();

        Assert.Equal(StoppedStatus.Unclear, verdict.Status);
        Assert.Equal((long?)1, verdict.ProjectId);
        Assert.Equal("project", verdict.ProjectName);
        Assert.Equal("c:\\work\\project", verdict.ProjectRoot);
        Assert.Equal("c:\\work\\project\\a.txt", verdict.LastFilePath);
        Assert.Null(verdict.Branch);
        Assert.Null(verdict.HeadCommit);
        Assert.Equal(0, verdict.DirtyCount);
    }

    [Fact]
    public async Task Dirty_files_give_maybe_unfinished()
    {
        var repository = SeedProjectScenario(out _, EventKind.GitCommit);
        var git = new StubGitClient
        {
            State = AvailableState(dirty: new DirtyFile("a.txt", FileChangeKind.Modified)),
        };
        var engine = CreateEngine(repository, git);

        var verdict = await engine.EvaluateAsync();

        // dirty > 0 - MaybeUnfinished, даже если последнее событие - коммит
        Assert.Equal(StoppedStatus.MaybeUnfinished, verdict.Status);
        Assert.Equal(1, verdict.DirtyCount);
    }

    [Fact]
    public async Task Clean_and_last_event_file_changed_gives_maybe_unfinished()
    {
        var repository = SeedProjectScenario(out _, EventKind.FileChanged);
        var git = new StubGitClient { State = AvailableState() };
        var engine = CreateEngine(repository, git);

        var verdict = await engine.EvaluateAsync();

        Assert.Equal(StoppedStatus.MaybeUnfinished, verdict.Status);
        Assert.Equal(0, verdict.DirtyCount);
    }

    [Fact]
    public async Task Clean_and_last_event_git_commit_gives_completed()
    {
        var repository = SeedProjectScenario(out _, EventKind.GitCommit);
        var git = new StubGitClient { State = AvailableState() };
        var engine = CreateEngine(repository, git);

        var verdict = await engine.EvaluateAsync();

        Assert.Equal(StoppedStatus.Completed, verdict.Status);
    }

    [Fact]
    public async Task Clean_and_last_event_other_gives_unclear()
    {
        // Чисто, но последнее событие проекта - ни правка, ни коммит
        var repository = SeedProjectScenario(out _, EventKind.AppFocused);
        var git = new StubGitClient { State = AvailableState() };
        var engine = CreateEngine(repository, git);

        var verdict = await engine.EvaluateAsync();

        Assert.Equal(StoppedStatus.Unclear, verdict.Status);
        Assert.Equal("main", verdict.Branch); // факты git заполнены и при Unclear
    }

    [Fact]
    public async Task Facts_are_filled_from_project_files_and_git()
    {
        var repository = SeedProjectScenario(out var projectId, EventKind.GitCommit);
        // Активность ДРУГОГО проекта не должна попасть в LastFilePath
        repository.FileActivities.Add(MakeActivity(99, 1, projectId + 100, "c:\\other\\x.cs"));
        var git = new StubGitClient { State = AvailableState() };
        var engine = CreateEngine(repository, git);

        var verdict = await engine.EvaluateAsync();

        Assert.Equal(projectId, verdict.ProjectId);
        Assert.Equal("project", verdict.ProjectName);
        Assert.Equal("c:\\work\\project", verdict.ProjectRoot);
        Assert.Equal("c:\\work\\project\\a.txt", verdict.LastFilePath);
        Assert.Equal("main", verdict.Branch);
        Assert.Equal(HeadHash, verdict.HeadCommit);
        Assert.Equal("fix: правка", verdict.HeadSubject);
        Assert.Equal(T0, verdict.HeadTs);
        Assert.Equal(0, verdict.DirtyCount);
    }

    [Fact]
    public async Task Explicit_session_id_selects_that_session()
    {
        var repository = new FakeRepository();
        repository.Sessions.Add(MakeSession(1));
        repository.Events.Add(MakeEvent(1, 1, EventKind.GitCommit, projectId: 1));
        repository.Projects.Add(MakeProject(1));
        repository.Sessions.Add(MakeSession(2)); // последняя сессия - пустая
        var git = new StubGitClient { State = AvailableState() };
        var engine = CreateEngine(repository, git);

        var verdict = await engine.EvaluateAsync(sessionId: 1);

        Assert.Equal(StoppedStatus.Completed, verdict.Status);
        Assert.Equal(1, verdict.ProjectId);
    }

    [Fact]
    public async Task Repository_failure_gives_unclear_without_exception()
    {
        var repository = new FakeRepository();
        var git = new StubGitClient();
        var engine = new StoppedEngine(
            new ThrowingRepository(repository), git, NullLogger<StoppedEngine>.Instance);

        var verdict = await engine.EvaluateAsync();

        AssertUnclearEmpty(verdict);
    }

    private static StoppedEngine CreateEngine(FakeRepository repository, StubGitClient git) =>
        new(repository, git, NullLogger<StoppedEngine>.Instance);

    private static void AssertUnclearEmpty(StoppedVerdict verdict)
    {
        Assert.Equal(StoppedStatus.Unclear, verdict.Status);
        Assert.Null(verdict.ProjectId);
        Assert.Null(verdict.ProjectName);
        Assert.Null(verdict.ProjectRoot);
        Assert.Null(verdict.LastFilePath);
        Assert.Null(verdict.Branch);
        Assert.Null(verdict.HeadCommit);
        Assert.Null(verdict.HeadSubject);
        Assert.Null(verdict.HeadTs);
        Assert.Equal(0, verdict.DirtyCount);
    }

    /// <summary>Сессия 1 с событием проекта 1 заданного вида и активностью файла проекта.</summary>
    private static FakeRepository SeedProjectScenario(out long projectId, EventKind lastKind)
    {
        var repository = new FakeRepository();
        repository.Sessions.Add(MakeSession(1));
        repository.Projects.Add(MakeProject(1));
        repository.Events.Add(MakeEvent(1, 1, EventKind.AppFocused, projectId: null));
        repository.Events.Add(MakeEvent(2, 1, lastKind, projectId: 1));
        repository.FileActivities.Add(MakeActivity(1, 1, 1, "c:\\work\\project\\a.txt"));
        projectId = 1;
        return repository;
    }

    private static Session MakeSession(long id) =>
        new(id, T0, T0.AddHours(1), SessionStatus.Ended, SessionEndReason.User, IdleThresholdSeconds: 900);

    private static Project MakeProject(long id) =>
        new(id, "project", "c:\\work\\project", GitRemote: null, T0.AddDays(-10), T0);

    private static Event MakeEvent(long id, long sessionId, EventKind kind, long? projectId) =>
        new(id, sessionId, T0.AddMinutes(id), kind, ApplicationId: null, projectId, Title: null, Path: null, Url: null, MetaJson: null);

    private static FileActivity MakeActivity(long id, long sessionId, long? projectId, string path) =>
        new(id, sessionId, T0.AddMinutes(id), projectId, path, FileChangeKind.Modified, FileActivitySource.Watcher);

    private static GitStateInfo AvailableState(params DirtyFile[] dirty) =>
        new(
            Available: true,
            Branch: "main",
            HeadCommit: HeadHash,
            HeadSubject: "fix: правка",
            HeadTimestamp: T0,
            DirtyFiles: dirty);

    /// <summary>Стаб git: отдаёт заданное состояние; по умолчанию недоступен.</summary>
    private sealed class StubGitClient : IGitClient
    {
        public GitStateInfo State { get; set; } = GitStateInfo.Unavailable;

        public string? FindGitExe() => "fake-git";

        public Task<string?> GetRepositoryRootAsync(string path, CancellationToken ct = default) =>
            Task.FromResult<string?>(path);

        public Task<GitStateInfo> GetStateAsync(string repositoryRoot, CancellationToken ct = default) =>
            Task.FromResult(State);
    }

    /// <summary>Обёртка над FakeRepository, падающая на первом же чтении.</summary>
    private sealed class ThrowingRepository : Continuum.Core.Interfaces.IRepository
    {
        private readonly Continuum.Core.Interfaces.IRepository _inner;

        public ThrowingRepository(Continuum.Core.Interfaces.IRepository inner) => _inner = inner;

        public Task<Session?> GetLastSessionAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("бд недоступна");

        public Task<long> CreateSessionAsync(Session session, CancellationToken ct = default) => _inner.CreateSessionAsync(session, ct);
        public Task EndSessionAsync(long sessionId, DateTimeOffset endedAt, SessionEndReason reason, CancellationToken ct = default) => _inner.EndSessionAsync(sessionId, endedAt, reason, ct);
        public Task<System.Collections.Generic.IReadOnlyList<Session>> GetOpenSessionsAsync(CancellationToken ct = default) => _inner.GetOpenSessionsAsync(ct);
        public Task AppendEventAsync(Event ev, CancellationToken ct = default) => _inner.AppendEventAsync(ev, ct);
        public Task<System.Collections.Generic.IReadOnlyList<Event>> GetSessionEventsAsync(long sessionId, CancellationToken ct = default) => _inner.GetSessionEventsAsync(sessionId, ct);
        public Task<DateTimeOffset?> GetLastEventTsAsync(long sessionId, CancellationToken ct = default) => _inner.GetLastEventTsAsync(sessionId, ct);
        public Task AppendFileActivityAsync(FileActivity activity, CancellationToken ct = default) => _inner.AppendFileActivityAsync(activity, ct);
        public Task<System.Collections.Generic.IReadOnlyList<FileActivity>> GetSessionFileActivityAsync(long sessionId, CancellationToken ct = default) => _inner.GetSessionFileActivityAsync(sessionId, ct);
        public Task<Project?> FindProjectByRootPathAsync(string rootPath, CancellationToken ct = default) => _inner.FindProjectByRootPathAsync(rootPath, ct);
        public Task<Project?> GetProjectAsync(long projectId, CancellationToken ct = default) => _inner.GetProjectAsync(projectId, ct);
        public Task<long> UpsertProjectAsync(Project project, CancellationToken ct = default) => _inner.UpsertProjectAsync(project, ct);
        public Task<long> UpsertApplicationAsync(ApplicationInfo application, CancellationToken ct = default) => _inner.UpsertApplicationAsync(application, ct);
        public Task<long> SaveSnapshotAsync(Snapshot snapshot, CancellationToken ct = default) => _inner.SaveSnapshotAsync(snapshot, ct);
        public Task<string?> GetSettingAsync(string key, CancellationToken ct = default) => _inner.GetSettingAsync(key, ct);
        public Task SetSettingAsync(string key, string value, CancellationToken ct = default) => _inner.SetSettingAsync(key, value, ct);
    }
}
