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

    public List<FileActivity> FileActivities { get; } = [];

    public List<Project> Projects { get; } = [];

    public List<Snapshot> Snapshots { get; } = [];

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

    public Task<DateTimeOffset?> GetLastEventTsAsync(long sessionId, CancellationToken ct = default)
    {
        var ts = Events.Where(e => e.SessionId == sessionId)
            .Select(e => (DateTimeOffset?)e.Ts)
            .OrderByDescending(t => t)
            .FirstOrDefault();
        return Task.FromResult(ts);
    }

    public Task AppendFileActivityAsync(FileActivity activity, CancellationToken ct = default)
    {
        FileActivities.Add(activity);
        return Task.CompletedTask;
    }

    public Task<Project?> FindProjectByRootPathAsync(string rootPath, CancellationToken ct = default)
        => Task.FromResult(Projects.FirstOrDefault(p => p.RootPath == rootPath));

    public Task<long> UpsertProjectAsync(Project project, CancellationToken ct = default)
    {
        var existing = Projects.FindIndex(p => p.RootPath == project.RootPath);
        if (existing >= 0)
        {
            Projects[existing] = Projects[existing] with { Name = project.Name, LastSeen = project.LastSeen };
            return Task.FromResult(Projects[existing].Id);
        }

        var created = project with { Id = _nextId++ };
        Projects.Add(created);
        return Task.FromResult(created.Id);
    }

    public Task<long> UpsertApplicationAsync(ApplicationInfo application, CancellationToken ct = default)
    {
        Applications.Add(application);
        return Task.FromResult(_nextId++);
    }

    public Task<long> SaveSnapshotAsync(Snapshot snapshot, CancellationToken ct = default)
    {
        var saved = snapshot with { Id = _nextId++ };
        Snapshots.Add(saved);
        return Task.FromResult(saved.Id);
    }

    public Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task SetSettingAsync(string key, string value, CancellationToken ct = default)
        => Task.CompletedTask;
}
