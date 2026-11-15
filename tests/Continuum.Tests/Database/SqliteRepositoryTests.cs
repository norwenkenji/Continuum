using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Infrastructure.Database;
using Xunit;

namespace Continuum.Tests.Database;

/// <summary>
/// Репозиторий на Entity Framework Core: круговые сценарии на временной БД -
/// сессии, события, активность файлов, upsert проектов и приложений,
/// снапшот с дочерними наборами, план восстановления, настройки.
/// </summary>
public sealed class SqliteRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 18, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "continuum-ef-repo", Guid.NewGuid().ToString("N"));

    private readonly SqliteRepository _repository;

    public SqliteRepositoryTests()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_dir, "test.db"));
        new DatabaseInitializer(factory).Initialize();
        _repository = new SqliteRepository(factory, new FixedClock(Now));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // уборка - best effort
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private Task<long> CreateSessionAsync() => _repository.CreateSessionAsync(
        new Session(0, Now, null, SessionStatus.Running, null, 900));

    [Fact]
    public async Task Session_lifecycle_roundtrip()
    {
        var id = await CreateSessionAsync();

        var last = await _repository.GetLastSessionAsync();
        Assert.NotNull(last);
        Assert.Equal(id, last!.Id);
        Assert.Equal(SessionStatus.Running, last.Status);
        Assert.Null(last.EndedAt);

        Assert.Single(await _repository.GetOpenSessionsAsync());

        await _repository.EndSessionAsync(id, Now.AddHours(1), SessionEndReason.User);

        var closed = await _repository.GetLastSessionAsync();
        Assert.Equal(SessionStatus.Ended, closed!.Status);
        Assert.Equal(SessionEndReason.User, closed.EndReason);
        Assert.Equal(Now.AddHours(1), closed.EndedAt);
        Assert.Empty(await _repository.GetOpenSessionsAsync());
    }

    [Fact]
    public async Task Create_session_always_starts_running()
    {
        // Контракт IRepository: статус и конец сессии во входной записи
        // игнорируются - сессия открывается жизненным циклом
        await _repository.CreateSessionAsync(new Session(
            0, Now, Now.AddHours(1), SessionStatus.Ended, SessionEndReason.User, 900));

        var session = await _repository.GetLastSessionAsync();
        Assert.Equal(SessionStatus.Running, session!.Status);
        Assert.Null(session.EndedAt);
        Assert.Null(session.EndReason);
    }

    [Fact]
    public async Task Events_are_appended_in_time_order()
    {
        var sessionId = await CreateSessionAsync();
        await _repository.AppendEventAsync(new Event(0, sessionId, Now.AddMinutes(2), EventKind.FileChanged, null, null, null, "d:/w/b.cs", null, null));
        await _repository.AppendEventAsync(new Event(0, sessionId, Now, EventKind.SessionStart, null, null, null, null, null, null));
        await _repository.AppendEventAsync(new Event(0, sessionId, Now.AddMinutes(1), EventKind.GitCommit, null, null, "abc1234 init", null, null, null));

        var events = await _repository.GetSessionEventsAsync(sessionId);

        Assert.Equal(3, events.Count);
        Assert.Equal(EventKind.SessionStart, events[0].Kind);
        Assert.Equal("abc1234 init", events[1].Title);
        Assert.Equal("d:/w/b.cs", events[2].Path);
        Assert.Equal(Now.AddMinutes(2), await _repository.GetLastEventTsAsync(sessionId));
    }

    [Fact]
    public async Task File_activity_roundtrip()
    {
        var sessionId = await CreateSessionAsync();
        await _repository.AppendFileActivityAsync(new FileActivity(0, sessionId, Now, null, "d:/w/a.cs", FileChangeKind.Modified, FileActivitySource.Watcher));
        await _repository.AppendFileActivityAsync(new FileActivity(0, sessionId, Now.AddSeconds(1), null, "d:/w/b.cs", FileChangeKind.Opened, FileActivitySource.Recent));

        var rows = await _repository.GetSessionFileActivityAsync(sessionId);

        Assert.Equal(2, rows.Count);
        Assert.Equal(FileActivitySource.Watcher, rows[0].Source);
        Assert.Equal(FileChangeKind.Opened, rows[1].ChangeKind);
    }

    [Fact]
    public async Task Project_upsert_updates_name_and_preserves_first_seen_and_remote()
    {
        var id = await _repository.UpsertProjectAsync(new Project(0, "Старое имя", "d:/work/proj", "git@host:repo.git", Now, Now));

        var updated = await _repository.UpsertProjectAsync(new Project(0, "Новое имя", "d:/work/proj", null, Now.AddHours(1), Now.AddHours(1)));

        Assert.Equal(id, updated);
        var project = await _repository.FindProjectByRootPathAsync("d:/work/proj");
        Assert.NotNull(project);
        Assert.Equal("Новое имя", project!.Name);
        Assert.Equal("git@host:repo.git", project.GitRemote);
        Assert.Equal(Now, project.FirstSeen);
        Assert.Equal(id, (await _repository.GetProjectAsync(id))!.Id);
    }

    [Fact]
    public async Task Application_upsert_deduplicates_by_name_and_exe()
    {
        var first = await _repository.UpsertApplicationAsync(new ApplicationInfo(0, "Блокнот", @"C:\Windows\notepad.exe", null, Now, Now));
        var second = await _repository.UpsertApplicationAsync(new ApplicationInfo(0, "Блокнот", @"C:\Windows\notepad.exe", "notepad", Now, Now));
        var other = await _repository.UpsertApplicationAsync(new ApplicationInfo(0, "Блокнот", null, null, Now, Now));

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public async Task Snapshot_with_children_is_saved_and_read_as_anchor()
    {
        var sessionId = await CreateSessionAsync();
        var projectId = await _repository.UpsertProjectAsync(new Project(0, "Proj", "d:/work/proj", null, Now, Now));

        var snapshotId = await _repository.SaveSnapshotAsync(new Snapshot(0, sessionId, Now, SnapshotReason.Timer, 1, "{\"projects\":1}")
        {
            GitStates = [new GitState(0, 0, projectId, true, "main", "abc1234", "init", Now, 2, "[{\"path\":\"a.cs\"}]", 1)],
            OpenFiles = [new OpenFile(0, 0, "d:/work/proj/a.cs", projectId, null, 42, Now, "vscode_state")],
            Terminals = [new TerminalState(0, 0, false, "unknown", null, null, TerminalSource.Unknown)],
        });

        var last = await _repository.GetLastSnapshotAsync(sessionId);

        Assert.NotNull(last);
        Assert.Equal(snapshotId, last!.Id);
        Assert.Equal(SnapshotReason.Timer, last.Reason);
        Assert.Equal("{\"projects\":1}", last.SummaryJson);
    }

    [Fact]
    public async Task Restore_plan_with_steps_roundtrip()
    {
        var sessionId = await CreateSessionAsync();
        var snapshotId = await _repository.SaveSnapshotAsync(
            new Snapshot(0, sessionId, Now, SnapshotReason.SessionEnd, 1, "{}"));

        var planId = await _repository.CreateRestorePlanAsync(new RestorePlan(0, snapshotId, Now, false, RestorePlanStatus.Pending, null, null)
        {
            Steps =
            [
                new RestoreStep(0, 0, 1, RestoreStrategyKind.StartProcess, "notepad", null, null, null, null),
                new RestoreStep(0, 0, 2, RestoreStrategyKind.OpenFile, "d:/w/a.cs", null, null, null, null),
            ],
        });

        var plan = await _repository.GetLastRestorePlanAsync(snapshotId);
        Assert.NotNull(plan);
        Assert.Equal(planId, plan!.Id);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal(1, plan.Steps[0].Seq);
        Assert.Equal(RestoreStrategyKind.StartProcess, plan.Steps[0].Strategy);
        Assert.Null(plan.Steps[0].Result);

        await _repository.SetRestorePlanStatusAsync(planId, RestorePlanStatus.Success, Now, Now.AddSeconds(2));
        await _repository.SetRestoreStepResultAsync(plan.Steps[0].Id, RestoreStepResult.Success, null, 120);

        var reloaded = await _repository.GetLastRestorePlanAsync(snapshotId);
        Assert.Equal(RestorePlanStatus.Success, reloaded!.Status);
        Assert.Equal(RestoreStepResult.Success, reloaded.Steps[0].Result);
        Assert.Equal(120, reloaded.Steps[0].DurationMs);
    }

    [Fact]
    public async Task Settings_are_upserted()
    {
        Assert.Null(await _repository.GetSettingAsync("demo_seeded"));

        await _repository.SetSettingAsync("demo_seeded", "0");
        Assert.Equal("0", await _repository.GetSettingAsync("demo_seeded"));

        await _repository.SetSettingAsync("demo_seeded", "1");
        Assert.Equal("1", await _repository.GetSettingAsync("demo_seeded"));
    }
}
