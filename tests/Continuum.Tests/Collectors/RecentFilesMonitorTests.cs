using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Continuum.Collectors;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Core.Time;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Continuum.Tests.Collectors;

/// <summary>
/// RecentFilesMonitor без реального %APPDATA%: папка Recent и разрешение
/// ярлыков подменяются конструктором (Func&lt;string, string?&gt;).
/// Проверяются базовый молчаливый снимок, дедупликация по (цель, mtime)
/// и фильтры целей.
/// </summary>
public class RecentFilesMonitorTests : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(60);

    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "continuum-recent-tests", Guid.NewGuid().ToString("N"));
    private readonly string _recentFolder;
    private readonly string _docsFolder;
    private readonly Dictionary<string, string?> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Observation> _events = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _resolveCounts = new();
    private readonly RecentFilesMonitor _monitor;

    public RecentFilesMonitorTests()
    {
        _recentFolder = Path.Combine(_tempRoot, "Recent");
        _docsFolder = Path.Combine(_tempRoot, "Docs");
        Directory.CreateDirectory(_recentFolder);
        Directory.CreateDirectory(_docsFolder);

        _monitor = new RecentFilesMonitor(
            new SystemClock(),
            NullLogger<RecentFilesMonitor>.Instance,
            _recentFolder,
            link =>
            {
                // Счётчик вызовов резолвера - маркер «тик прошёл по этому ярлыку»:
                // базовый тик ждём детерминированно, а не фиксированным сном
                // (при параллельной нагрузке первый тик опаздывал дольше сна,
                // свежий ярлык попадал в базовый снимок - тест краснел по таймауту)
                _resolveCounts.AddOrUpdate(link, 1, static (_, count) => count + 1);
                return _targets.TryGetValue(link, out var target) ? target : null;
            },
            PollInterval);
        _monitor.Observed += (_, observation) =>
        {
            lock (_events)
            {
                _events.Add(observation);
            }
        };
    }

    [Fact]
    public void First_poll_is_baseline_and_silent()
    {
        var target = CreateDocument("a.txt");
        CreateLink("a.lnk", target);
        _monitor.Start();

        Thread.Sleep(400); // несколько тиков опроса

        lock (_events)
        {
            Assert.Empty(_events);
        }
    }

    [Fact]
    public void New_link_after_baseline_emits_file_opened()
    {
        StartAndWaitBaseline();

        var target = CreateDocument("b.txt");
        CreateLink("b.lnk", target);

        Assert.True(WaitFor(() => CountFor(target) == 1));
        var observation = FirstFor(target)!;
        Assert.Equal(EventKind.FileOpened, observation.EventKind);
        Assert.Equal(FileChangeKind.Opened, observation.FileChange);
        Assert.Equal(FileActivitySource.Recent, observation.FileSource);
        Assert.Equal(target, observation.Observable.Value);
        Assert.Null(observation.Observable.ProjectRootPath);
    }

    [Fact]
    public void Same_mtime_does_not_re_emit()
    {
        StartAndWaitBaseline();

        var target = CreateDocument("c.txt");
        var link = CreateLink("c.lnk", target);
        Assert.True(WaitFor(() => CountFor(target) == 1));

        Thread.Sleep(400); // несколько тиков без изменений mtime

        Assert.Equal(1, CountFor(target));
    }

    [Fact]
    public void Changed_mtime_emits_again()
    {
        StartAndWaitBaseline();

        var target = CreateDocument("d.txt");
        var link = CreateLink("d.lnk", target);
        Assert.True(WaitFor(() => CountFor(target) == 1));

        // Ярлык обновился - новое открытие того же документа
        File.SetLastWriteTime(link, DateTime.Now.AddMinutes(1));

        Assert.True(WaitFor(() => CountFor(target) == 2));
    }

    [Fact]
    public void Broken_and_invalid_links_are_skipped()
    {
        StartAndWaitBaseline();

        // Битый ярлык: разрешение вернуло null
        var broken = Path.Combine(_recentFolder, "broken.lnk");
        File.WriteAllText(broken, "x");
        _targets[broken] = null;

        // Цель не существует
        var missing = Path.Combine(_recentFolder, "missing.lnk");
        File.WriteAllText(missing, "x");
        _targets[missing] = Path.Combine(_docsFolder, "no-such-file.txt");

        // Цель внутри самой папки Recent
        var inner = Path.Combine(_recentFolder, "inner.lnk");
        File.WriteAllText(inner, "x");
        var innerTarget = Path.Combine(_recentFolder, "self.lnk");
        File.WriteAllText(innerTarget, "x");
        _targets[inner] = innerTarget;

        // Цель - каталог, а не файл
        var dirLink = Path.Combine(_recentFolder, "dir.lnk");
        File.WriteAllText(dirLink, "x");
        _targets[dirLink] = _docsFolder;

        Thread.Sleep(500); // несколько тиков

        lock (_events)
        {
            Assert.Empty(_events);
        }
    }

    /// <summary>
    /// Стартует монитор и детерминированно ждёт базовый тик: ярлык-сентинел
    /// создаётся до старта, база считается пройденной, когда резолвер был
    /// вызван для него. Фиксированный сон здесь заменён сознательно.
    /// </summary>
    private void StartAndWaitBaseline()
    {
        var sentinel = CreateLink("zz-sentinel.lnk", CreateDocument("zz-sentinel.txt"));
        _monitor.Start();
        Assert.True(WaitFor(() => _resolveCounts.TryGetValue(sentinel, out var count) && count >= 1),
            "базовый тик не прошёл за 15 секунд");
    }

    private string CreateDocument(string name)
    {
        var path = Path.Combine(_docsFolder, name);
        File.WriteAllText(path, "content");
        return path;
    }

    private string CreateLink(string name, string target)
    {
        var path = Path.Combine(_recentFolder, name);
        File.WriteAllText(path, "fake-link");
        _targets[path] = target;
        return path;
    }

    private int CountFor(string target)
    {
        lock (_events)
        {
            return _events.Count(o => o.Observable.Value == target);
        }
    }

    private Observation? FirstFor(string target)
    {
        lock (_events)
        {
            return _events.FirstOrDefault(o => o.Observable.Value == target);
        }
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 15000)
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

    public void Dispose()
    {
        _monitor.Dispose();
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
