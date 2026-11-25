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

    private readonly List<RestorePlan> _restorePlans = [];

    public IReadOnlyList<RestorePlan> RestorePlans => _restorePlans;

    // Настройки хранятся по-настоящему: тесты идемпотентности (demo_seeded)
    // проверяют ключ после записи; при отсутствии ключа - null, как и раньше
    private readonly Dictionary<string, string> _settings = new(StringComparer.Ordinal);

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
        => Task.FromResult<IReadOnlyList<Event>>(
            [.. Events.Where(e => e.SessionId == sessionId).OrderBy(e => e.Ts)]);

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

    public Task<IReadOnlyList<FileActivity>> GetSessionFileActivityAsync(long sessionId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<FileActivity>>(
            [.. FileActivities.Where(f => f.SessionId == sessionId).OrderBy(f => f.Ts)]);

    public Task<Project?> FindProjectByRootPathAsync(string rootPath, CancellationToken ct = default)
        => Task.FromResult(Projects.FirstOrDefault(p => p.RootPath == rootPath));

    public Task<Project?> GetProjectAsync(long projectId, CancellationToken ct = default)
        => Task.FromResult(Projects.FirstOrDefault(p => p.Id == projectId));

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
        var exe = application.ExePath ?? string.Empty;
        var existing = Applications.FindIndex(a =>
            string.Equals(a.Name, application.Name, StringComparison.Ordinal)
            && string.Equals(a.ExePath ?? string.Empty, exe, StringComparison.Ordinal));
        if (existing >= 0)
        {
            Applications[existing] = Applications[existing] with
            {
                AdapterKey = application.AdapterKey ?? Applications[existing].AdapterKey,
                LastSeen = application.LastSeen,
            };
            return Task.FromResult(Applications[existing].Id);
        }

        var created = application with { Id = _nextId++ };
        Applications.Add(created);
        return Task.FromResult(created.Id);
    }

    public Task<IReadOnlyList<OpenFile>> GetSnapshotOpenFilesAsync(long snapshotId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<OpenFile>>(
            Snapshots.Where(s => s.Id == snapshotId).SelectMany(s => s.OpenFiles).ToList());

    public Task<ApplicationInfo?> GetApplicationAsync(long applicationId, CancellationToken ct = default)
        => Task.FromResult(Applications.FirstOrDefault(a => a.Id == applicationId));

    public Task<int> DeleteSessionsOlderThanAsync(DateTimeOffset threshold, CancellationToken ct = default)
    {
        var victims = Sessions.Where(s => s.EndedAt is { } endedAt && endedAt < threshold).ToList();
        if (victims.Count == 0)
        {
            return Task.FromResult(0);
        }

        var ids = victims.Select(s => s.Id).ToHashSet();
        Events.RemoveAll(e => ids.Contains(e.SessionId));
        FileActivities.RemoveAll(f => ids.Contains(f.SessionId));
        var snapshotIds = Snapshots.Where(s => ids.Contains(s.SessionId)).Select(s => s.Id).ToHashSet();
        Snapshots.RemoveAll(s => ids.Contains(s.SessionId));
        _restorePlans.RemoveAll(p => snapshotIds.Contains(p.SnapshotId));
        Sessions.RemoveAll(s => ids.Contains(s.Id));
        return Task.FromResult(victims.Count);
    }

    public Task<IReadOnlyList<BrowserTab>> GetSnapshotBrowserTabsAsync(long snapshotId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<BrowserTab>>(
            Snapshots.Where(s => s.Id == snapshotId).SelectMany(s => s.BrowserTabs).ToList());

    public Task<long> SaveSnapshotAsync(Snapshot snapshot, CancellationToken ct = default)
    {
        var saved = snapshot with { Id = _nextId++ };
        Snapshots.Add(saved);
        return Task.FromResult(saved.Id);
    }

    public Task<Snapshot?> GetLastSnapshotAsync(long sessionId, CancellationToken ct = default)
        => Task.FromResult(Snapshots.Where(s => s.SessionId == sessionId).OrderByDescending(s => s.Ts).ThenByDescending(s => s.Id).FirstOrDefault());

    public Task<long> CreateRestorePlanAsync(RestorePlan plan, CancellationToken ct = default)
    {
        var planId = _nextId++;
        var steps = plan.Steps.Select((s, i) => s with { Id = _nextId++, PlanId = planId }).ToList();
        _restorePlans.Add(plan with { Id = planId, Steps = steps });
        return Task.FromResult(planId);
    }

    public Task SetRestorePlanStatusAsync(long planId, RestorePlanStatus status, DateTimeOffset? startedAt, DateTimeOffset? finishedAt, CancellationToken ct = default)
    {
        var index = _restorePlans.FindIndex(p => p.Id == planId);
        if (index >= 0)
        {
            _restorePlans[index] = _restorePlans[index] with { Status = status, StartedAt = startedAt, FinishedAt = finishedAt };
        }

        return Task.CompletedTask;
    }

    public Task SetRestoreStepResultAsync(long stepId, RestoreStepResult result, string? error, long? durationMs, CancellationToken ct = default)
    {
        foreach (var plan in _restorePlans)
        {
            var index = plan.Steps.ToList().FindIndex(s => s.Id == stepId);
            if (index >= 0)
            {
                var steps = plan.Steps.ToList();
                steps[index] = steps[index] with { Result = result, Error = error, DurationMs = durationMs };
                _restorePlans[_restorePlans.IndexOf(plan)] = plan with { Steps = steps };
                break;
            }
        }

        return Task.CompletedTask;
    }

    public Task<RestorePlan?> GetLastRestorePlanAsync(long snapshotId, CancellationToken ct = default)
        => Task.FromResult(_restorePlans.Where(p => p.SnapshotId == snapshotId).OrderByDescending(p => p.Id).FirstOrDefault());

    public Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
        => Task.FromResult(_settings.TryGetValue(key, out var value) ? value : null);

    public Task SetSettingAsync(string key, string value, CancellationToken ct = default)
    {
        _settings[key] = value;
        return Task.CompletedTask;
    }
}
