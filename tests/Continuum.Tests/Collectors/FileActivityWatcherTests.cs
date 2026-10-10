using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Collectors;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Core.Time;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Continuum.Tests.Collectors;

/// <summary>
/// FileActivityWatcher на реальной временной папке: эмиссия по факту,
/// исключённые каталоги, дедупликация, идемпотентность Start/Stop.
/// Ожидание событий - циклом с таймаутом, не фиксированным Sleep.
/// </summary>
public class FileActivityWatcherTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "continuum-watcher-tests", Guid.NewGuid().ToString("N"));

    public FileActivityWatcherTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void New_file_emits_added_event_with_watcher_source()
    {
        var events = new List<Observation>();
        using var watcher = CreateWatcher(events);
        watcher.Start();

        var file = Path.Combine(_tempRoot, "main.cs");
        File.WriteAllText(file, "class A {}");

        Assert.True(WaitFor(() => LockedFind(events, file, FileChangeKind.Added) is not null));
        var observation = LockedFind(events, file, FileChangeKind.Added)!;

        Assert.Equal(EventKind.FileChanged, observation.EventKind);
        Assert.Equal(FileActivitySource.Watcher, observation.FileSource);
        Assert.Equal(ObservableKind.FilePath, observation.Observable.Kind);
        Assert.Equal(_tempRoot, observation.Observable.ProjectRootPath);
    }

    [Fact]
    public void Excluded_directory_does_not_emit()
    {
        var events = new List<Observation>();
        using var watcher = CreateWatcher(events);
        watcher.Start();

        // Сначала доказываем, что наблюдатель жив
        var alive = Path.Combine(_tempRoot, "alive.cs");
        File.WriteAllText(alive, "x");
        Assert.True(WaitFor(() => LockedFind(events, alive, FileChangeKind.Added) is not null));

        var binDir = Path.Combine(_tempRoot, "bin");
        Directory.CreateDirectory(binDir);
        var excluded = Path.Combine(binDir, "app.dll");
        File.WriteAllText(excluded, "x");

        // Даём событиям время дойти и убеждаемся: исключённого пути нет
        Thread.Sleep(1500);
        lock (events)
        {
            Assert.DoesNotContain(events, o =>
                o.Observable.Value is not null &&
                o.Observable.Value.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
        }
    }

    [Fact]
    public void Rapid_repeated_changes_emit_once_per_dedup_window()
    {
        var events = new List<Observation>();
        using var watcher = CreateWatcher(events);
        watcher.Start();

        var file = Path.Combine(_tempRoot, "work.txt");
        File.WriteAllText(file, "one");
        Assert.True(WaitFor(() => LockedFind(events, file, FileChangeKind.Added) is not null));

        // Два быстрых изменения подряд - одно событие Modified
        File.AppendAllText(file, "two");
        Assert.True(WaitFor(() => LockedCount(events, file, FileChangeKind.Modified) >= 1));
        File.AppendAllText(file, "three");
        Thread.Sleep(700); // в пределах окна дедупликации (2 с)

        Assert.Equal(1, LockedCount(events, file, FileChangeKind.Modified));
    }

    [Fact]
    public void Start_and_stop_are_idempotent()
    {
        var events = new List<Observation>();
        using var watcher = CreateWatcher(events);

        watcher.Start();
        watcher.Start();
        watcher.Stop();
        watcher.Stop();
    }

    [Fact]
    public void After_stop_no_events_are_emitted()
    {
        var events = new List<Observation>();
        using var watcher = CreateWatcher(events);
        watcher.Start();

        var alive = Path.Combine(_tempRoot, "alive.cs");
        File.WriteAllText(alive, "x");
        Assert.True(WaitFor(() => LockedFind(events, alive, FileChangeKind.Added) is not null));

        watcher.Stop();
        lock (events)
        {
            events.Clear();
        }

        File.WriteAllText(Path.Combine(_tempRoot, "after-stop.cs"), "x");
        Thread.Sleep(1200);

        lock (events)
        {
            Assert.Empty(events);
        }
    }

    private FileActivityWatcher CreateWatcher(List<Observation> sink)
    {
        var watcher = new FileActivityWatcher(
            new StubProjectRegistry(_tempRoot),
            new SystemClock(),
            NullLogger<FileActivityWatcher>.Instance,
            TimeSpan.FromMilliseconds(200));
        watcher.Observed += (_, observation) =>
        {
            lock (sink)
            {
                sink.Add(observation);
            }
        };
        return watcher;
    }

    private static Observation? LockedFind(List<Observation> events, string path, FileChangeKind kind)
    {
        lock (events)
        {
            return events.FirstOrDefault(o =>
                o.Observable.Value == path && o.FileChange == kind);
        }
    }

    private static int LockedCount(List<Observation> events, string path, FileChangeKind kind)
    {
        lock (events)
        {
            return events.Count(o => o.Observable.Value == path && o.FileChange == kind);
        }
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(25);
        }

        return condition();
    }

    /// <summary>Стаб-реестр проектов: фиксированный набор корней, БД не нужна.</summary>
    private sealed class StubProjectRegistry : IProjectRegistry
    {
        private readonly string[] _roots;

        public StubProjectRegistry(params string[] roots) => _roots = roots;

        public IReadOnlyCollection<string> RegisteredRoots => _roots;

        public Task<long?> RegisterPathAsync(string path, CancellationToken ct = default) =>
            Task.FromResult<long?>(null);

        public long? GetProjectId(string normalizedRoot) => null;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
