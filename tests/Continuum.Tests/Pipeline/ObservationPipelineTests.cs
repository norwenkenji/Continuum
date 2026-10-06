using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Pipeline;
using Continuum.Runtime;
using Continuum.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Continuum.Tests.Pipeline;

public class ObservationPipelineTests
{
    private sealed class StubFilter(bool allow) : IPrivacyFilter
    {
        public bool Allow(Observable o) => allow;
    }

    private sealed class StubSanitizer : ISanitizer
    {
        public ObservableKind? LastKind { get; private set; }

        public string Sanitize(string value, ObservableKind kind)
        {
            LastKind = kind;
            return value.Replace("secret", "***", StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class StubSession(long id) : ISessionContext
    {
        public long CurrentSessionId => id;
    }

    private sealed class StubRegistry : IProjectRegistry
    {
        private long _nextId = 100;

        public System.Collections.Generic.IReadOnlyCollection<string> RegisteredRoots => [];

        public int RegisterCalls { get; private set; }

        public string? LastRegisteredPath { get; private set; }

        public Task<long?> RegisterPathAsync(string path, CancellationToken ct = default)
        {
            RegisterCalls++;
            LastRegisteredPath = path;
            return Task.FromResult<long?>(_nextId++);
        }

        public long? GetProjectId(string normalizedRoot) => null;
    }

    private static ObservationPipeline MakePipeline(
        FakeRepository repository,
        StubRegistry? registry = null,
        IPrivacyFilter? filter = null,
        StubSanitizer? sanitizer = null,
        long sessionId = 1) => new(
        filter ?? new StubFilter(true),
        sanitizer ?? new StubSanitizer(),
        repository,
        new StubSession(sessionId),
        registry ?? new StubRegistry(),
        NullLogger<ObservationPipeline>.Instance);

    private static Observable MakeObservable(ObservableKind kind, string? value, string? projectRoot = null) => new(
        Kind: kind,
        Value: value,
        ProcessId: 123,
        ApplicationName: "notepad",
        ApplicationExePath: "C:\\Windows\\notepad.exe",
        Timestamp: new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero),
        ProjectRootPath: projectRoot);

    [Fact]
    public async Task Denied_observation_persists_nothing()
    {
        var repository = new FakeRepository();
        var registry = new StubRegistry();
        var pipeline = MakePipeline(repository, registry, filter: new StubFilter(false));

        await pipeline.HandleAsync(new Observation(MakeObservable(ObservableKind.WindowTitle, "title"), EventKind.AppFocused));

        Assert.Empty(repository.Events);
        Assert.Empty(repository.Applications);
        Assert.Equal(0, registry.RegisterCalls);
    }

    [Fact]
    public async Task Allowed_window_title_is_sanitized_and_app_upserted()
    {
        var repository = new FakeRepository();
        var sanitizer = new StubSanitizer();
        var pipeline = MakePipeline(repository, sanitizer: sanitizer, sessionId: 7);

        await pipeline.HandleAsync(
            new Observation(MakeObservable(ObservableKind.WindowTitle, "doc.txt - secret"), EventKind.AppFocused));

        var ev = Assert.Single(repository.Events);
        Assert.Equal(EventKind.AppFocused, ev.Kind);
        Assert.Equal(7, ev.SessionId);
        Assert.Equal("doc.txt - ***", ev.Title);
        Assert.Null(ev.Path);
        Assert.Null(ev.Url);
        Assert.NotNull(ev.ApplicationId);
        Assert.Null(ev.ProjectId);

        var app = Assert.Single(repository.Applications);
        Assert.Equal("notepad", app.Name);
        Assert.Equal("C:\\Windows\\notepad.exe", app.ExePath);
        Assert.Equal(ObservableKind.WindowTitle, sanitizer.LastKind);
    }

    [Fact]
    public async Task Command_line_goes_to_meta_json()
    {
        var repository = new FakeRepository();
        var pipeline = MakePipeline(repository);

        await pipeline.HandleAsync(
            new Observation(MakeObservable(ObservableKind.CommandLine, "app.exe --password=secret"), EventKind.AppStarted));

        var ev = Assert.Single(repository.Events);
        Assert.Null(ev.Title);
        Assert.Equal("{\"cmd\":\"app.exe --password=***\"}", ev.MetaJson);
    }

    [Fact]
    public async Task File_path_goes_to_path_column()
    {
        var repository = new FakeRepository();
        var pipeline = MakePipeline(repository);

        await pipeline.HandleAsync(
            new Observation(
                MakeObservable(ObservableKind.FilePath, "C:\\work\\main.cs"),
                EventKind.FileOpened,
                FileChange: FileChangeKind.Opened,
                FileSource: FileActivitySource.Recent));

        var ev = Assert.Single(repository.Events);
        Assert.Equal("C:\\work\\main.cs", ev.Path);
        Assert.Null(ev.Title);
    }

    [Fact]
    public async Task File_observation_writes_file_activity_with_project()
    {
        var repository = new FakeRepository();
        var registry = new StubRegistry();
        var pipeline = MakePipeline(repository, registry, sessionId: 7);

        await pipeline.HandleAsync(
            new Observation(
                MakeObservable(ObservableKind.FilePath, "C:\\work\\main.cs", projectRoot: "C:\\work"),
                EventKind.FileChanged,
                FileChange: FileChangeKind.Modified,
                FileSource: FileActivitySource.Git));

        var ev = Assert.Single(repository.Events);
        Assert.Equal(EventKind.FileChanged, ev.Kind);
        Assert.NotNull(ev.ProjectId);
        Assert.Equal("C:\\work", registry.LastRegisteredPath);

        var activity = Assert.Single(repository.FileActivities);
        Assert.Equal(7, activity.SessionId);
        Assert.Equal("C:\\work\\main.cs", activity.Path);
        Assert.Equal(FileChangeKind.Modified, activity.ChangeKind);
        Assert.Equal(FileActivitySource.Git, activity.Source);
        Assert.Equal(ev.ProjectId, activity.ProjectId);
    }

    [Fact]
    public async Task File_event_without_change_or_source_is_dropped_with_warning()
    {
        var repository = new FakeRepository();
        var pipeline = MakePipeline(repository);

        await pipeline.HandleAsync(
            new Observation(MakeObservable(ObservableKind.FilePath, "C:\\work\\a.cs"), EventKind.FileChanged));

        // Событие хронологии остаётся, но строки file_activity быть не должно:
        // без change_kind/source запись бессмысленна
        Assert.Single(repository.Events);
        Assert.Empty(repository.FileActivities);
    }

    [Fact]
    public async Task Non_file_events_never_write_file_activity()
    {
        var repository = new FakeRepository();
        var pipeline = MakePipeline(repository);

        await pipeline.HandleAsync(
            new Observation(MakeObservable(ObservableKind.WindowTitle, "t"), EventKind.AppFocused));

        Assert.Empty(repository.FileActivities);
    }

    [Fact]
    public async Task Repository_failure_is_swallowed()
    {
        var repository = new FakeRepository
        {
            OnAppendEvent = _ => throw new InvalidOperationException("бд упала"),
        };
        var pipeline = MakePipeline(repository);

        // Не бросает: рекордер не имеет права ронять приложение
        await pipeline.HandleAsync(
            new Observation(MakeObservable(ObservableKind.WindowTitle, "t"), EventKind.AppFocused));
    }
}
