using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Core.Interfaces;
using Continuum.Infrastructure.Database.Ef;
using Microsoft.EntityFrameworkCore;

namespace Continuum.Infrastructure.Database;

/// <summary>
/// Хранилище Continuum поверх SQLite через Entity Framework Core (DbContext,
/// LINQ, миграции EF Core). Ручного SQL нет: выборки - LINQ, запись -
/// SaveChanges (снапшот и план со всеми дочерними наборами пишутся одной
/// транзакцией EF). Времена - unix-секунды UTC; enum - snake_case через
/// <see cref="EnumCodec"/> (конвертеры в <see cref="ContinuumDbContext"/>).
/// </summary>
public sealed class SqliteRepository : IRepository
{
    private readonly SqliteConnectionFactory _connections;
    private readonly IClock _clock;

    public SqliteRepository(SqliteConnectionFactory connections, IClock clock)
    {
        _connections = connections;
        _clock = clock;
    }

    // ---------------- Сессии ----------------

    public async Task<long> CreateSessionAsync(Session session, CancellationToken ct = default)
    {
        await using var db = new ContinuumDbContext(_connections);
        var row = new SessionRow
        {
            StartedAt = Unix(session.StartedAt),
            EndedAt = null,
            Status = SessionStatus.Running,
            EndReason = null,
            IdleThresholdSeconds = session.IdleThresholdSeconds,
        };

        db.Sessions.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row.Id;
    }

    public async Task EndSessionAsync(long sessionId, DateTimeOffset endedAt, SessionEndReason reason, CancellationToken ct = default)
    {
        await using var db = new ContinuumDbContext(_connections);
        var row = await db.Sessions.FirstOrDefaultAsync(x => x.Id == sessionId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return;
        }

        row.EndedAt = Unix(endedAt);
        row.Status = SessionStatus.Ended;
        row.EndReason = reason;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Session>> GetOpenSessionsAsync(CancellationToken ct = default)
    {
        await using var db = new ContinuumDbContext(_connections);
        var rows = await db.Sessions
            .AsNoTracking()
            .Where(x => x.EndedAt == null)
            .OrderBy(x => x.StartedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(MapSession).ToList();
    }

    public async Task<Session?> GetLastSessionAsync(CancellationToken ct = default)
    {
        await using var db = new ContinuumDbContext(_connections);
        var row = await db.Sessions
            .AsNoTracking()
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return row is null ? null : MapSession(row);
    }

    // ---------------- События ----------------

    public async Task AppendEventAsync(Event ev, CancellationToken ct = default)
    {
        await using var db = new ContinuumDbContext(_connections);
        db.Events.Add(new EventRow
        {
            SessionId = ev.SessionId,
            Ts = Unix(ev.Ts),
            Kind = ev.Kind,
            ApplicationId = ev.ApplicationId,
            ProjectId = ev.ProjectId,
            Title = ev.Title,
            Path = ev.Path,
            Url = ev.Url,
            MetaJson = ev.MetaJson,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Event>> GetSessionEventsAsync(long sessionId, CancellationToken ct = default)
    {
        await using var db = new ContinuumDbContext(_connections);
        var rows = await db.Events
            .AsNoTracking()
            .Where(x => x.SessionId == sessionId)
            .OrderBy(x => x.Ts)
            .ThenBy(x => x.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(r => new Event(
            Id: r.Id,
            SessionId: r.SessionId,
            Ts: Ts(r.Ts),
            Kind: r.Kind,
            ApplicationId: r.ApplicationId,
            ProjectId: r.ProjectId,
            Title: r.Title,
            Path: r.Path,
            Url: r.Url,
            MetaJson: r.MetaJson)).ToList();
    }

    public async Task<DateTimeOffset?> GetLastEventTsAsync(long sessionId, CancellationToken ct = default)
    {
        await using var db = new ContinuumDbContext(_connections);
        var last = await db.Events
            .AsNoTracking()
            .Where(x => x.SessionId == sessionId)
            .Select(x => (long?)x.Ts)
            .MaxAsync(ct)
            .ConfigureAwait(false);
        return last is null ? null : Ts(last.Value);
    }

    // ---------------- Активность файлов ----------------

    public async Task AppendFileActivityAsync(FileActivity activity, CancellationToken ct = default)
    {
        await using var db = new ContinuumDbContext(_connections);
        db.FileActivity.Add(new FileActivityRow
        {
            SessionId = activity.SessionId,
            Ts = Unix(activity.Ts),
            ProjectId = activity.ProjectId,
            Path = activity.Path,
            ChangeKind = activity.ChangeKind,
            Source = activity.Source,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FileActivity>> GetSessionFileActivityAsync(long sessionId, CancellationToken ct = default)
    {
        await using var db = new ContinuumDbContext(_connections);
        var rows = await db.FileActivity
            .AsNoTracking()
            .Where(x => x.SessionId == sessionId)
            .OrderBy(x => x.Ts)
            .ThenBy(x => x.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(r => new FileActivity(
            Id: r.Id,
            SessionId: r.SessionId,
            Ts: Ts(r.Ts),
            ProjectId: r.ProjectId,
            Path: r.Path,
            ChangeKind: r.ChangeKind,
            Source: r.Source)).ToList();
    }

    // ---------------- Проекты и приложения ----------------

    public async Task<Project?> FindProjectByRootPathAsync(string rootPath, CancellationToken ct = default)
    {
        await using var db = new ContinuumDbContext(_connections);
        var row = await db.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.RootPath == rootPath, ct)
            .ConfigureAwait(false);
        return row is null ? null : MapProject(row);
    }

    public async Task<Project?> GetProjectAsync(long projectId, CancellationToken ct = default)
    {
        await using var db = new ContinuumDbContext(_connections);
        var row = await db.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == projectId, ct)
            .ConfigureAwait(false);
        return row is null ? null : MapProject(row);
    }

    public async Task<long> UpsertProjectAsync(Project project, CancellationToken ct = default)
    {
        await using var db = new ContinuumDbContext(_connections);
        var seen = Unix(_clock.UtcNow);
        var row = await db.Projects
            .FirstOrDefaultAsync(x => x.RootPath == project.RootPath, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new ProjectRow
            {
                Name = project.Name,
                RootPath = project.RootPath,
                GitRemote = project.GitRemote,
                FirstSeen = seen,
                LastSeen = seen,
            };
            db.Projects.Add(row);
        }
        else
        {
            // Совпадает с прежним ON CONFLICT: name и last_seen обновляются,
            // git_remote - только если пришло непустое значение
            row.Name = project.Name;
            row.GitRemote = project.GitRemote ?? row.GitRemote;
            row.LastSeen = seen;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row.Id;
    }

    public async Task<long> UpsertApplicationAsync(ApplicationInfo application, CancellationToken ct = default)
    {
        await using var db = new ContinuumDbContext(_connections);
        var seen = Unix(_clock.UtcNow);
        var exePath = application.ExePath ?? string.Empty;
        var row = await db.Applications
            .FirstOrDefaultAsync(x => x.Name == application.Name && x.ExePath == exePath, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new ApplicationRow
            {
                Name = application.Name,
                ExePath = exePath,
                AdapterKey = application.AdapterKey,
                FirstSeen = seen,
                LastSeen = seen,
            };
            db.Applications.Add(row);
        }
        else
        {
            row.AdapterKey = application.AdapterKey ?? row.AdapterKey;
            row.LastSeen = seen;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row.Id;
    }

    // ---------------- Снапшоты ----------------

    public async Task<long> SaveSnapshotAsync(Snapshot snapshot, CancellationToken ct = default)
    {
        await using var db = new ContinuumDbContext(_connections);

        var row = new SnapshotRow
        {
            SessionId = snapshot.SessionId,
            Ts = Unix(snapshot.Ts),
            Reason = snapshot.Reason,
            SchemaVersion = snapshot.SchemaVersion,
            SummaryJson = snapshot.SummaryJson,
        };

        foreach (var git in snapshot.GitStates)
        {
            row.GitStates.Add(new GitStateRow
            {
                ProjectId = git.ProjectId,
                Available = git.Available,
                Branch = git.Branch,
                HeadCommit = git.HeadCommit,
                HeadSubject = git.HeadSubject,
                HeadTs = git.HeadTs is { } headTs ? Unix(headTs) : null,
                DirtyCount = git.DirtyCount,
                DirtyFilesJson = git.DirtyFilesJson,
                UntrackedCount = git.UntrackedCount,
            });
        }

        foreach (var terminal in snapshot.Terminals)
        {
            row.Terminals.Add(new TerminalRow
            {
                Available = terminal.Available,
                Host = terminal.Host,
                Shell = terminal.Shell,
                Cwd = terminal.Cwd,
                Source = terminal.Source,
            });
        }

        foreach (var file in snapshot.OpenFiles)
        {
            row.OpenFiles.Add(new OpenFileRow
            {
                Path = file.Path,
                ProjectId = file.ProjectId,
                ApplicationId = file.ApplicationId,
                Line = file.Line,
                LastModified = file.LastModified is { } lastModified ? Unix(lastModified) : null,
                Source = file.Source,
            });
        }

        foreach (var tab in snapshot.BrowserTabs)
        {
            row.BrowserTabs.Add(new BrowserTabRow
            {
                ApplicationId = tab.ApplicationId,
                Url = tab.Url,
                Title = tab.Title,
                UrlAvailable = tab.UrlAvailable,
                IsActive = tab.IsActive,
                Source = tab.Source,
            });
        }

        // Один SaveChanges - одна транзакция: снапшот и все дочерние наборы
        // либо записаны целиком, либо не записаны вовсе
        db.Snapshots.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row.Id;
    }

    public async Task<Snapshot?> GetLastSnapshotAsync(long sessionId, CancellationToken ct = default)
    {
        await using var db = new ContinuumDbContext(_connections);
        var row = await db.Snapshots
            .AsNoTracking()
            .Where(x => x.SessionId == sessionId)
            .OrderByDescending(x => x.Ts)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // Дочерние наборы не грузим: снапшот нужен как якорь (id/время) - см. RestoreService
        return row is null
            ? null
            : new Snapshot(row.Id, row.SessionId, Ts(row.Ts), row.Reason, row.SchemaVersion, row.SummaryJson);
    }

    // ---------------- Планы восстановления ----------------

    public async Task<long> CreateRestorePlanAsync(RestorePlan plan, CancellationToken ct = default)
    {
        await using var db = new ContinuumDbContext(_connections);

        var row = new RestorePlanRow
        {
            SnapshotId = plan.SnapshotId,
            CreatedAt = Unix(plan.CreatedAt),
            DryRun = plan.DryRun,
            Status = plan.Status,
            StartedAt = plan.StartedAt is { } startedAt ? Unix(startedAt) : null,
            FinishedAt = plan.FinishedAt is { } finishedAt ? Unix(finishedAt) : null,
        };

        foreach (var step in plan.Steps)
        {
            row.Steps.Add(new RestoreStepRow
            {
                Seq = step.Seq,
                Strategy = step.Strategy,
                Target = step.Target,
                ArgsJson = step.ArgsJson,
                Result = step.Result,
                Error = step.Error,
                DurationMs = step.DurationMs,
            });
        }

        db.RestorePlans.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row.Id;
    }

    public async Task SetRestorePlanStatusAsync(long planId, RestorePlanStatus status, DateTimeOffset? startedAt, DateTimeOffset? finishedAt, CancellationToken ct = default)
    {
        await using var db = new ContinuumDbContext(_connections);
        var row = await db.RestorePlans.FirstOrDefaultAsync(x => x.Id == planId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return;
        }

        row.Status = status;
        row.StartedAt = startedAt is { } started ? Unix(started) : null;
        row.FinishedAt = finishedAt is { } finished ? Unix(finished) : null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SetRestoreStepResultAsync(long stepId, RestoreStepResult result, string? error, long? durationMs, CancellationToken ct = default)
    {
        await using var db = new ContinuumDbContext(_connections);
        var row = await db.RestoreSteps.FirstOrDefaultAsync(x => x.Id == stepId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return;
        }

        row.Result = result;
        row.Error = error;
        row.DurationMs = durationMs;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<RestorePlan?> GetLastRestorePlanAsync(long snapshotId, CancellationToken ct = default)
    {
        await using var db = new ContinuumDbContext(_connections);
        var row = await db.RestorePlans
            .AsNoTracking()
            .Include(x => x.Steps)
            .Where(x => x.SnapshotId == snapshotId)
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        var steps = row.Steps
            .OrderBy(x => x.Seq)
            .Select(s => new RestoreStep(
                Id: s.Id,
                PlanId: s.PlanId,
                Seq: s.Seq,
                Strategy: s.Strategy,
                Target: s.Target,
                ArgsJson: s.ArgsJson,
                Result: s.Result,
                Error: s.Error,
                DurationMs: s.DurationMs))
            .ToList();

        return new RestorePlan(
            Id: row.Id,
            SnapshotId: row.SnapshotId,
            CreatedAt: Ts(row.CreatedAt),
            DryRun: row.DryRun,
            Status: row.Status,
            StartedAt: row.StartedAt is { } started ? Ts(started) : null,
            FinishedAt: row.FinishedAt is { } finished ? Ts(finished) : null)
        {
            Steps = steps,
        };
    }

    // ---------------- Настройки ----------------

    public async Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
    {
        await using var db = new ContinuumDbContext(_connections);
        var row = await db.Settings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == key, ct)
            .ConfigureAwait(false);
        return row?.Value;
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken ct = default)
    {
        await using var db = new ContinuumDbContext(_connections);
        var row = await db.Settings.FirstOrDefaultAsync(x => x.Key == key, ct).ConfigureAwait(false);
        if (row is null)
        {
            db.Settings.Add(new SettingRow { Key = key, Value = value, UpdatedAt = Unix(_clock.UtcNow) });
        }
        else
        {
            row.Value = value;
            row.UpdatedAt = Unix(_clock.UtcNow);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ---------------- Маппинг ----------------

    private static DateTimeOffset Ts(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds);

    private static long Unix(DateTimeOffset value) => value.ToUnixTimeSeconds();

    private static Session MapSession(SessionRow row) => new(
        Id: row.Id,
        StartedAt: Ts(row.StartedAt),
        EndedAt: row.EndedAt is { } endedAt ? Ts(endedAt) : null,
        Status: row.Status,
        EndReason: row.EndReason,
        IdleThresholdSeconds: row.IdleThresholdSeconds);

    private static Project MapProject(ProjectRow row) => new(
        Id: row.Id,
        Name: row.Name,
        RootPath: row.RootPath,
        GitRemote: row.GitRemote,
        FirstSeen: row.FirstSeen is { } firstSeen ? Ts(firstSeen) : DateTimeOffset.UnixEpoch,
        LastSeen: row.LastSeen is { } lastSeen ? Ts(lastSeen) : DateTimeOffset.UnixEpoch);
}
