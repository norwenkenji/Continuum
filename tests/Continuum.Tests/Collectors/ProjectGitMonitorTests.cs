using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// ProjectGitMonitor на фейковом IGitClient: события GitCommit и
/// GitBranchSwitch по диффу HEAD/ветки, недоступный git не сбрасывает
/// состояние.
/// </summary>
public class ProjectGitMonitorTests
{
    private const string Root = "c:\\work\\project";
    private const string Commit1 = "1111111111111111111111111111111111111111";
    private const string Commit2 = "2222222222222222222222222222222222222222";

    [Fact]
    public void Commit_after_baseline_emits_exactly_one_git_commit_event()
    {
        var git = new FakeGitClient();
        git.SetState(Root, State("main", Commit1, "initial"));
        var events = new List<Observation>();
        using var monitor = CreateMonitor(git, events);
        monitor.Start();
        WaitBaseline();

        git.SetState(Root, State("main", Commit2, "feat: новая работа"));

        Assert.True(WaitFor(() => LockedCount(events, EventKind.GitCommit) == 1));
        var observation = LockedFirst(events, EventKind.GitCommit)!;
        Assert.Equal("2222222 feat: новая работа", observation.Observable.Value);
        Assert.Equal(ObservableKind.GitRef, observation.Observable.Kind);
        Assert.Equal(Root, observation.Observable.ProjectRootPath);
        Assert.Null(observation.FileChange);
        Assert.Null(observation.FileSource);

        // Повторные тики без изменений - событие не дублируется
        Thread.Sleep(300);
        Assert.Equal(1, LockedCount(events, EventKind.GitCommit));
    }

    [Fact]
    public void Branch_switch_emits_git_branch_switch_event()
    {
        var git = new FakeGitClient();
        git.SetState(Root, State("main", Commit1, "initial"));
        var events = new List<Observation>();
        using var monitor = CreateMonitor(git, events);
        monitor.Start();
        WaitBaseline();

        git.SetState(Root, State("feature", Commit1, "initial"));

        Assert.True(WaitFor(() => LockedCount(events, EventKind.GitBranchSwitch) == 1));
        var observation = LockedFirst(events, EventKind.GitBranchSwitch)!;
        Assert.Equal("main -> feature", observation.Observable.Value);
        Assert.Equal(Root, observation.Observable.ProjectRootPath);
        Assert.Empty(LockedWhere(events, o => o.EventKind == EventKind.GitCommit));
    }

    [Fact]
    public void Unavailable_git_does_not_reset_state()
    {
        var git = new FakeGitClient();
        git.SetState(Root, State("main", Commit1, "initial"));
        var events = new List<Observation>();
        using var monitor = CreateMonitor(git, events);
        monitor.Start();
        WaitBaseline();

        // Промежуточные тики без git - состояние не сбрасывается
        git.SetUnavailable(Root);
        Thread.Sleep(250);
        Assert.Empty(events);

        // git вернулся с новым коммитом - ровно одно событие, а не «новая база»
        git.SetState(Root, State("main", Commit2, "after outage"));
        Assert.True(WaitFor(() => LockedCount(events, EventKind.GitCommit) == 1));
        Thread.Sleep(300);
        Assert.Equal(1, LockedCount(events, EventKind.GitCommit));
    }

    [Fact]
    public void Baseline_tick_is_silent()
    {
        var git = new FakeGitClient();
        git.SetState(Root, State("main", Commit1, "initial"));
        var events = new List<Observation>();
        using var monitor = CreateMonitor(git, events);
        monitor.Start();

        Thread.Sleep(300); // несколько тиков

        lock (events)
        {
            Assert.Empty(events);
        }
    }

    private static GitStateInfo State(string? branch, string? headCommit, string? headSubject) =>
        new(
            Available: true,
            Branch: branch,
            HeadCommit: headCommit,
            HeadSubject: headSubject,
            HeadTimestamp: new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
            DirtyFiles: []);

    private ProjectGitMonitor CreateMonitor(FakeGitClient git, List<Observation> sink)
    {
        var monitor = new ProjectGitMonitor(
            new StubProjectRegistry(Root),
            git,
            new SystemClock(),
            NullLogger<ProjectGitMonitor>.Instance,
            TimeSpan.FromMilliseconds(50));
        monitor.Observed += (_, observation) =>
        {
            lock (sink)
            {
                sink.Add(observation);
            }
        };
        return monitor;
    }

    private static void WaitBaseline() => Thread.Sleep(200);

    private static int LockedCount(List<Observation> events, EventKind kind)
    {
        lock (events)
        {
            return events.Count(o => o.EventKind == kind);
        }
    }

    private static Observation? LockedFirst(List<Observation> events, EventKind kind)
    {
        lock (events)
        {
            return events.FirstOrDefault(o => o.EventKind == kind);
        }
    }

    private static List<Observation> LockedWhere(List<Observation> events, Func<Observation, bool> predicate)
    {
        lock (events)
        {
            return events.Where(predicate).ToList();
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

    /// <summary>Фейковый git: состояние корня задаётся из теста.</summary>
    private sealed class FakeGitClient : IGitClient
    {
        private readonly Dictionary<string, GitStateInfo> _states = new(StringComparer.Ordinal);

        public void SetState(string root, GitStateInfo state)
        {
            lock (_states)
            {
                _states[root] = state;
            }
        }

        public void SetUnavailable(string root)
        {
            lock (_states)
            {
                _states[root] = GitStateInfo.Unavailable;
            }
        }

        public string? FindGitExe() => "fake-git";

        public Task<string?> GetRepositoryRootAsync(string path, CancellationToken ct = default) =>
            Task.FromResult<string?>(path);

        public Task<GitStateInfo> GetStateAsync(string repositoryRoot, CancellationToken ct = default)
        {
            lock (_states)
            {
                return Task.FromResult(
                    _states.TryGetValue(repositoryRoot, out var state) ? state : GitStateInfo.Unavailable);
            }
        }
    }

    /// <summary>Стаб-реестр проектов: фиксированный набор корней.</summary>
    private sealed class StubProjectRegistry : IProjectRegistry
    {
        private readonly string[] _roots;

        public StubProjectRegistry(params string[] roots) => _roots = roots;

        public IReadOnlyCollection<string> RegisteredRoots => _roots;

        public Task<long?> RegisterPathAsync(string path, CancellationToken ct = default) =>
            Task.FromResult<long?>(null);

        public long? GetProjectId(string normalizedRoot) => null;
    }
}
