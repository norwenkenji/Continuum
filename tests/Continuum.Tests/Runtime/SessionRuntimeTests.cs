using System;
using System.Linq;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Runtime;
using Continuum.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Continuum.Tests.Runtime;

public class SessionRuntimeTests
{
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private static SessionRuntime CreateRuntime(FakeRepository repository) => new(
        repository, new FixedClock(Now), NullLogger<SessionRuntime>.Instance);

    [Fact]
    public async Task Start_closes_stale_sessions_and_opens_new()
    {
        var repository = new FakeRepository();
        // Две висячие сессии «прошлых запусков»
        await repository.CreateSessionAsync(new Session(0, Now.AddHours(-3), null, SessionStatus.Running, null, 900));
        await repository.CreateSessionAsync(new Session(0, Now.AddHours(-1), null, SessionStatus.Running, null, 900));

        var runtime = CreateRuntime(repository);
        await runtime.StartAsync();

        Assert.Equal(3, repository.Sessions.Count);
        Assert.Equal(2, repository.Sessions.Count(s =>
            s.Status == SessionStatus.Ended && s.EndReason == SessionEndReason.CrashRecovered));

        var current = repository.Sessions.Single(s => s.EndedAt is null);
        Assert.Equal(SessionStatus.Running, current.Status);
        Assert.Equal(SessionRuntime.DefaultIdleThresholdSeconds, current.IdleThresholdSeconds);
        Assert.Equal(current.Id, runtime.CurrentSessionId);

        var start = Assert.Single(repository.Events);
        Assert.Equal(EventKind.SessionStart, start.Kind);
        Assert.Equal(current.Id, start.SessionId);
    }

    [Fact]
    public async Task Stop_appends_session_end_and_closes_session()
    {
        var repository = new FakeRepository();
        var runtime = CreateRuntime(repository);
        await runtime.StartAsync();

        await runtime.StopAsync(SessionEndReason.User);

        var session = Assert.Single(repository.Sessions);
        Assert.Equal(SessionStatus.Ended, session.Status);
        Assert.Equal(SessionEndReason.User, session.EndReason);
        Assert.Equal(Now, session.EndedAt);
        Assert.Equal(0, runtime.CurrentSessionId);

        Assert.Equal(
            new[] { EventKind.SessionStart, EventKind.SessionEnd },
            repository.Events.Select(e => e.Kind).ToArray());
    }

    [Fact]
    public async Task Stop_twice_is_noop()
    {
        var repository = new FakeRepository();
        var runtime = CreateRuntime(repository);
        await runtime.StartAsync();

        await runtime.StopAsync(SessionEndReason.User);
        await runtime.StopAsync(SessionEndReason.User);

        Assert.Equal(2, repository.Events.Count);
        Assert.Equal(1, repository.Sessions.Count(s => s.Status == SessionStatus.Ended));
    }
}
