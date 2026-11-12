using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Demo;
using Continuum.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Continuum.Tests.Demo;

/// <summary>
/// DemoSeeder на FakeRepository и фиксированных часах: демо-корень в %TEMP%.
/// Проверяются файлы демо-проекта, одна завершённая сессия, ровно 45 событий
/// хронологически, нормализация корня проекта, file_activity, снапшот с честным
/// git=недоступен, идемпотентность по ключу demo_seeded и поведение при сбое.
/// </summary>
public class DemoSeederTests : IDisposable
{
    /// <summary>Фиксированное «сейчас»: посев детерминирован до секунды.</summary>
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 18, 30, 0, TimeSpan.Zero);

    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "continuum-demo-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeRepository _repository = new();
    private readonly string _demoRoot;

    public DemoSeederTests()
    {
        _demoRoot = Path.Combine(_tempRoot, "DemoProject");
    }

    [Fact]
    public async Task First_seed_creates_non_empty_project_files()
    {
        var seeded = await CreateSeeder().SeedAsync();

        Assert.True(seeded);
        AssertNonEmpty(Path.Combine(_demoRoot, "README.md"));
        AssertNonEmpty(Path.Combine(_demoRoot, "src", "Program.cs"));
        AssertNonEmpty(Path.Combine(_demoRoot, "src", "Main.cs"));
        AssertNonEmpty(Path.Combine(_demoRoot, "project.config"));
    }

    [Fact]
    public async Task Seed_creates_single_ended_session_with_exact_bounds()
    {
        await CreateSeeder().SeedAsync();

        var session = Assert.Single(_repository.Sessions);
        Assert.Equal(SessionStatus.Ended, session.Status);
        Assert.Equal(SessionEndReason.User, session.EndReason);
        Assert.Equal(Now - TimeSpan.FromHours(2), session.StartedAt);
        Assert.Equal(Now - TimeSpan.FromMinutes(25), session.EndedAt);
        Assert.Equal(900, session.IdleThresholdSeconds);
    }

    [Fact]
    public async Task Seed_creates_45_chronological_events()
    {
        await CreateSeeder().SeedAsync();
        var session = _repository.Sessions.Single();

        var events = await _repository.GetSessionEventsAsync(session.Id);

        Assert.Equal(45, events.Count);
        for (var i = 1; i < events.Count; i++)
        {
            Assert.True(events[i - 1].Ts <= events[i].Ts,
                $"Событие {i} нарушает порядок хронологии");
        }

        Assert.All(events, e =>
        {
            Assert.True(e.Ts >= session.StartedAt, "Событие раньше начала сессии");
            Assert.True(e.Ts <= session.EndedAt, "Событие позже конца сессии");
        });
        Assert.Equal(EventKind.SessionStart, events[0].Kind);
        Assert.Equal(EventKind.SessionEnd, events[^1].Kind);

        // Правки указывают на существующие файлы и привязаны к проекту
        var projectId = _repository.Projects.Single().Id;
        var changed = events.Where(e => e.Kind == EventKind.FileChanged).ToList();
        Assert.Equal(17, changed.Count);
        Assert.All(changed, e =>
        {
            Assert.NotNull(e.Path);
            Assert.True(File.Exists(e.Path), $"Путь file_changed не существует: {e.Path}");
            Assert.Equal(projectId, e.ProjectId);
        });
    }

    [Fact]
    public async Task Seed_normalizes_project_root()
    {
        await CreateSeeder().SeedAsync();

        var project = Assert.Single(_repository.Projects);
        Assert.Equal("DemoProject", project.Name);
        Assert.Null(project.GitRemote);

        var root = project.RootPath;
        Assert.EndsWith("demoproject", root); // имя папки в нижнем регистре
        Assert.Equal(root, root.ToLowerInvariant()); // весь путь в нижнем регистре
        Assert.False(root.EndsWith(Path.DirectorySeparatorChar), "Хвостовой разделитель в корне");
    }

    [Fact]
    public async Task Seed_file_activity_has_existing_paths_and_sources()
    {
        await CreateSeeder().SeedAsync();
        var session = _repository.Sessions.Single();
        var projectId = _repository.Projects.Single().Id;

        var activities = await _repository.GetSessionFileActivityAsync(session.Id);

        var watcher = activities.Where(a => a.Source == FileActivitySource.Watcher).ToList();
        var recent = activities.Where(a => a.Source == FileActivitySource.Recent).ToList();
        Assert.Equal(17, watcher.Count);
        Assert.Equal(3, recent.Count);
        Assert.All(watcher, a => Assert.Equal(FileChangeKind.Modified, a.ChangeKind));
        Assert.All(recent, a => Assert.Equal(FileChangeKind.Opened, a.ChangeKind));
        Assert.All(activities, a => Assert.Equal(session.Id, a.SessionId));
        Assert.All(activities, a => Assert.Equal(projectId, a.ProjectId));
        Assert.All(activities, a => Assert.True(File.Exists(a.Path), $"Путь file_activity не существует: {a.Path}"));
    }

    [Fact]
    public async Task Seed_creates_snapshot_with_honest_git_and_open_files()
    {
        await CreateSeeder().SeedAsync();
        var session = _repository.Sessions.Single();
        var projectId = _repository.Projects.Single().Id;

        var snapshot = Assert.Single(_repository.Snapshots);

        Assert.Equal(session.Id, snapshot.SessionId);
        Assert.Equal(session.EndedAt, snapshot.Ts);
        Assert.Equal(SnapshotReason.SessionEnd, snapshot.Reason);
        Assert.Equal(1, snapshot.SchemaVersion);

        // Проект без git в демо - честно: available = 0 (вердикт «Статус неясен»)
        var gitState = Assert.Single(snapshot.GitStates);
        Assert.False(gitState.Available);
        Assert.Equal(projectId, gitState.ProjectId);

        Assert.Equal(2, snapshot.OpenFiles.Count);
        var readme = snapshot.OpenFiles.Single(f => f.Line == 42);
        Assert.Equal(Path.Combine(_demoRoot, "README.md"), readme.Path);
        Assert.Equal(projectId, readme.ProjectId);
        Assert.Equal("vscode_state", readme.Source);
        Assert.Equal(session.EndedAt, readme.LastModified);

        var program = snapshot.OpenFiles.Single(f => f.Line == 10);
        Assert.Equal(Path.Combine(_demoRoot, "src", "Program.cs"), program.Path);
        Assert.Equal("vscode_state", program.Source);
    }

    [Fact]
    public async Task Second_seed_is_noop()
    {
        Assert.True(await CreateSeeder().SeedAsync());
        var sessions = _repository.Sessions.Count;
        var events = _repository.Events.Count;
        var activities = _repository.FileActivities.Count;
        var snapshots = _repository.Snapshots.Count;
        var projects = _repository.Projects.Count;

        Assert.False(await CreateSeeder().SeedAsync());

        Assert.Equal(sessions, _repository.Sessions.Count);
        Assert.Equal(events, _repository.Events.Count);
        Assert.Equal(activities, _repository.FileActivities.Count);
        Assert.Equal(snapshots, _repository.Snapshots.Count);
        Assert.Equal(projects, _repository.Projects.Count);
    }

    [Fact]
    public async Task Failed_seed_returns_false_and_leaves_key_unset()
    {
        _repository.OnAppendEvent = _ => Task.FromException(new InvalidOperationException("сбой записи события"));

        var seeded = await CreateSeeder().SeedAsync();

        Assert.False(seeded);
        Assert.Null(await _repository.GetSettingAsync("demo_seeded"));
        Assert.Empty(_repository.Events); // событие сбоем не записано
    }

    [Fact]
    public async Task Existing_files_are_not_overwritten()
    {
        Directory.CreateDirectory(_demoRoot);
        var readmePath = Path.Combine(_demoRoot, "README.md");
        File.WriteAllText(readmePath, "МАРКЕР: не перезаписывать");

        Assert.True(await CreateSeeder().SeedAsync());

        Assert.Equal("МАРКЕР: не перезаписывать", File.ReadAllText(readmePath));
        // Остальные файлы создались, снапшот и события на местах
        Assert.Equal(45, _repository.Events.Count);
        Assert.Single(_repository.Snapshots);
    }

    private DemoSeeder CreateSeeder() =>
        new(_repository, new FixedClock(Now), NullLogger<DemoSeeder>.Instance, _demoRoot);

    private static void AssertNonEmpty(string path)
    {
        Assert.True(File.Exists(path), $"Файл демо-проекта не создан: {path}");
        Assert.True(new FileInfo(path).Length > 0, $"Файл демо-проекта пуст: {path}");
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
