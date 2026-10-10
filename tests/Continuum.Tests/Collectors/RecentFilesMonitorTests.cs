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
            link => _targets.TryGetValue(link, out var target) ? target : null,
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
        _monitor.Start();
        WaitBaseline();

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
        _monitor.Start();
        WaitBaseline();

        var target = CreateDocument("c.txt");
        var link = CreateLink("c.lnk", target);
        Assert.True(WaitFor(() => CountFor(target) == 1));

        Thread.Sleep(400); // несколько тиков без изменений mtime

        Assert.Equal(1, CountFor(target));
    }

    [Fact]
    public void Changed_mtime_emits_again()
    {
        _monitor.Start();
        WaitBaseline();

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
        _monitor.Start();
        WaitBaseline();

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

    /// <summary>Ждёт прохождения базового тика (первый опрос - молчаливый снимок).</summary>
    private static void WaitBaseline() => Thread.Sleep(250);

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

    public void Dispose()
    {
        _monitor.Dispose();
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
