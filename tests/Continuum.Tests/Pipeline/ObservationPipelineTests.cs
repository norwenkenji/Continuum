using System;
using System.Linq;
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

    private static Observable MakeObservable(ObservableKind kind, string? value) => new(
        Kind: kind,
        Value: value,
        ProcessId: 123,
        ApplicationName: "notepad",
        ApplicationExePath: "C:\\Windows\\notepad.exe",
        Timestamp: new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero),
        ProjectRootPath: null);

    [Fact]
    public async Task Denied_observation_persists_nothing()
    {
        var repository = new FakeRepository();
        var pipeline = new ObservationPipeline(
            new StubFilter(false), new StubSanitizer(), repository, new StubSession(7),
            NullLogger<ObservationPipeline>.Instance);

        await pipeline.HandleAsync(new Observation(MakeObservable(ObservableKind.WindowTitle, "title"), EventKind.AppFocused));

        Assert.Empty(repository.Events);
        Assert.Empty(repository.Applications);
    }

    [Fact]
    public async Task Allowed_window_title_is_sanitized_and_app_upserted()
    {
        var repository = new FakeRepository();
        var sanitizer = new StubSanitizer();
        var pipeline = new ObservationPipeline(
            new StubFilter(true), sanitizer, repository, new StubSession(7),
            NullLogger<ObservationPipeline>.Instance);

        await pipeline.HandleAsync(
            new Observation(MakeObservable(ObservableKind.WindowTitle, "doc.txt - secret"), EventKind.AppFocused));

        var ev = Assert.Single(repository.Events);
        Assert.Equal(EventKind.AppFocused, ev.Kind);
        Assert.Equal(7, ev.SessionId);
        Assert.Equal("doc.txt - ***", ev.Title);
        Assert.Null(ev.Path);
        Assert.Null(ev.Url);
        Assert.NotNull(ev.ApplicationId);

        var app = Assert.Single(repository.Applications);
        Assert.Equal("notepad", app.Name);
        Assert.Equal("C:\\Windows\\notepad.exe", app.ExePath);
        Assert.Equal(ObservableKind.WindowTitle, sanitizer.LastKind);
    }

    [Fact]
    public async Task Command_line_goes_to_meta_json()
    {
        var repository = new FakeRepository();
        var pipeline = new ObservationPipeline(
            new StubFilter(true), new StubSanitizer(), repository, new StubSession(1),
            NullLogger<ObservationPipeline>.Instance);

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
        var pipeline = new ObservationPipeline(
            new StubFilter(true), new StubSanitizer(), repository, new StubSession(1),
            NullLogger<ObservationPipeline>.Instance);

        await pipeline.HandleAsync(
            new Observation(MakeObservable(ObservableKind.FilePath, "C:\\work\\main.cs"), EventKind.FileOpened));

        var ev = Assert.Single(repository.Events);
        Assert.Equal("C:\\work\\main.cs", ev.Path);
        Assert.Null(ev.Title);
    }

    [Fact]
    public async Task Repository_failure_is_swallowed()
    {
        var repository = new FakeRepository
        {
            OnAppendEvent = _ => throw new InvalidOperationException("бд упала"),
        };
        var pipeline = new ObservationPipeline(
            new StubFilter(true), new StubSanitizer(), repository, new StubSession(1),
            NullLogger<ObservationPipeline>.Instance);

        // Не бросает: рекордер не имеет права ронять приложение
        await pipeline.HandleAsync(
            new Observation(MakeObservable(ObservableKind.WindowTitle, "t"), EventKind.AppFocused));
    }
}
