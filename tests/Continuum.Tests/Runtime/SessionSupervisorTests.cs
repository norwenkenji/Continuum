using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Runtime;
using Continuum.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Continuum.Tests.Runtime;

public class SessionSupervisorTests
{
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class FakeIdle : IIdleTimeSource
    {
        public TimeSpan Idle { get; set; } = TimeSpan.Zero;
        public TimeSpan GetIdleTime() => Idle;
    }

    private sealed class FakeSnapshotter : ISnapshotter
    {
        public List<SnapshotReason> Reasons { get; } = [];
        public Task<long> SaveSnapshotAsync(SnapshotReason reason, CancellationToken ct = default)
        {
            Reasons.Add(reason);
            return Task.FromResult(1L);
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static (SessionSupervisor, SessionRuntime, FakeRepository, FakeIdle, FakeSnapshotter) Create()
    {
        var repo = new FakeRepository();
        var idle = new FakeIdle();
        var snapshotter = new FakeSnapshotter();
        var runtime = new SessionRuntime(repo, new FixedClock(Now), NullLogger<SessionRuntime>.Instance);
        var supervisor = new SessionSupervisor(
            runtime, repo, idle, snapshotter, new FixedClock(Now),
            NullLogger<SessionSupervisor>.Instance,
            idleThreshold: TimeSpan.FromMinutes(15),
            pollInterval: TimeSpan.FromMilliseconds(30));
        return (supervisor, runtime, repo, idle, snapshotter);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("условие не наступило за 5 секунд");
    }

    [Fact]
    public async Task Idle_over_threshold_closes_session_with_snapshot()
    {
        var (supervisor, runtime, repo, idle, snapshotter) = Create();
        await runtime.StartAsync();
        supervisor.Start();
        try
        {
            idle.Idle = TimeSpan.FromMinutes(16);
            await WaitForAsync(() => runtime.CurrentSessionId == 0);

            var session = Assert.Single(repo.Sessions);
            Assert.Equal(SessionStatus.Ended, session.Status);
            Assert.Equal(SessionEndReason.Idle, session.EndReason);
            Assert.Equal([SnapshotReason.SessionEnd], snapshotter.Reasons);
        }
        finally
        {
            supervisor.Stop();
        }
    }

    [Fact]
    public async Task Input_return_after_idle_opens_new_session()
    {
        var (supervisor, runtime, repo, idle, _) = Create();
        await runtime.StartAsync();
        var firstId = runtime.CurrentSessionId;
        supervisor.Start();
        try
        {
            idle.Idle = TimeSpan.FromMinutes(20);
            await WaitForAsync(() => runtime.CurrentSessionId == 0);

            idle.Idle = TimeSpan.Zero; // пользователь вернулся
            await WaitForAsync(() => runtime.CurrentSessionId != 0);

            Assert.Equal(2, repo.Sessions.Count);
            Assert.NotEqual(firstId, runtime.CurrentSessionId);
            Assert.Equal(2, repo.Events.Count(e => e.Kind == EventKind.SessionStart));
        }
        finally
        {
            supervisor.Stop();
        }
    }

    [Fact]
    public async Task Active_user_keeps_session_open()
    {
        var (supervisor, runtime, repo, _, snapshotter) = Create();
        await runtime.StartAsync();
        supervisor.Start();
        try
        {
            await Task.Delay(150); // несколько тиков при активном вводе
            Assert.NotEqual(0, runtime.CurrentSessionId);
            Assert.Single(repo.Sessions, s => s.EndedAt is null);
            Assert.Empty(snapshotter.Reasons);
        }
        finally
        {
            supervisor.Stop();
        }
    }

    [Fact]
    public async Task Sleep_and_wake_write_events_without_breaking_session()
    {
        var (supervisor, runtime, repo, _, _) = Create();
        await runtime.StartAsync();
        var sessionId = runtime.CurrentSessionId;

        supervisor.HandlePowerMode(Microsoft.Win32.PowerModes.Suspend);
        supervisor.HandlePowerMode(Microsoft.Win32.PowerModes.Resume);
        supervisor.HandlePowerMode(Microsoft.Win32.PowerModes.StatusChange); // игнорируется

        Assert.Equal(
            new[] { EventKind.SessionStart, EventKind.SystemSleep, EventKind.SystemWake },
            repo.Events.Select(e => e.Kind).ToArray());
        Assert.Equal(sessionId, runtime.CurrentSessionId); // сессия не порвалась
    }

    [Fact]
    public async Task Sleep_events_skipped_without_active_session()
    {
        var (supervisor, runtime, repo, _, _) = Create();
        Assert.Equal(0, runtime.CurrentSessionId);

        supervisor.HandlePowerMode(Microsoft.Win32.PowerModes.Suspend);

        Assert.Empty(repo.Events);
    }
}
