using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Domain;
using Continuum.Core.Interfaces;

namespace Continuum.Tests.Fakes;

/// <summary>In-memory заглушка IRepository для тестов пайплайна и сессии.</summary>
internal sealed class FakeRepository : IRepository
{
    private long _nextId = 1;

    public List<Session> Sessions { get; } = [];

    public List<Event> Events { get; } = [];

    public List<ApplicationInfo> Applications { get; } = [];

    public Func<Event, Task>? OnAppendEvent { get; set; }

    public Task<long> CreateSessionAsync(Session session, CancellationToken ct = default)
    {
        var created = session with { Id = _nextId++ };
        Sessions.Add(created);
        return Task.FromResult(created.Id);
    }

    public Task EndSessionAsync(long sessionId, DateTimeOffset endedAt, SessionEndReason reason, CancellationToken ct = default)
    {
        var index = Sessions.FindIndex(s => s.Id == sessionId);
        if (index >= 0)
        {
            Sessions[index] = Sessions[index] with
            {
                EndedAt = endedAt,
                Status = SessionStatus.Ended,
                EndReason = reason,
            };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Session>> GetOpenSessionsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Session>>([.. Sessions.Where(s => s.EndedAt is null)]);

    public Task<Session?> GetLastSessionAsync(CancellationToken ct = default)
        => Task.FromResult(Sessions.OrderByDescending(s => s.Id).FirstOrDefault());

    public Task AppendEventAsync(Event ev, CancellationToken ct = default)
    {
        if (OnAppendEvent is not null)
        {
            return OnAppendEvent(ev);
        }

        Events.Add(ev);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Event>> GetSessionEventsAsync(long sessionId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Event>>([.. Events.Where(e => e.SessionId == sessionId)]);

    public Task AppendFileActivityAsync(FileActivity activity, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<Project?> FindProjectByRootPathAsync(string rootPath, CancellationToken ct = default)
        => Task.FromResult<Project?>(null);

    public Task<long> UpsertProjectAsync(Project project, CancellationToken ct = default)
        => Task.FromResult(_nextId++);

    public Task<long> UpsertApplicationAsync(ApplicationInfo application, CancellationToken ct = default)
    {
        Applications.Add(application);
        return Task.FromResult(_nextId++);
    }

    public Task<long> SaveSnapshotAsync(Snapshot snapshot, CancellationToken ct = default)
        => Task.FromResult(_nextId++);

    public Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task SetSettingAsync(string key, string value, CancellationToken ct = default)
        => Task.CompletedTask;
}
