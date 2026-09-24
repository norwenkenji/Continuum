using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Core.Interfaces;
using Microsoft.Data.Sqlite;

namespace Continuum.Infrastructure.Database;

/// <summary>
/// Хранилище поверх SQLite (Microsoft.Data.Sqlite, без EF).
/// Времена — unix-секунды UTC; enum — snake_case через <see cref="EnumCodec"/>.
/// Снапшот пишется со всеми дочерними наборами в одной транзакции.
/// </summary>
public sealed class SqliteRepository : IRepository
{
    private readonly SqliteConnectionFactory _factory;
    private readonly IClock _clock;

    public SqliteRepository(SqliteConnectionFactory factory, IClock clock)
    {
        _factory = factory;
        _clock = clock;
    }

    // ---------------- Сессии ----------------

    public async Task<long> CreateSessionAsync(Session session, CancellationToken ct = default)
    {
        await using var connection = _factory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions (started_at, ended_at, status, end_reason, idle_threshold_s)
            VALUES ($started_at, NULL, $status, NULL, $idle_threshold_s);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$started_at", session.StartedAt.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$status", EnumCodec.ToDb(SessionStatus.Running));
        command.Parameters.AddWithValue("$idle_threshold_s", session.IdleThresholdSeconds);
        return (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    public async Task EndSessionAsync(long sessionId, DateTimeOffset endedAt, SessionEndReason reason, CancellationToken ct = default)
    {
        await using var connection = _factory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sessions
            SET ended_at = $ended_at, status = $status, end_reason = $end_reason
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$ended_at", endedAt.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$status", EnumCodec.ToDb(SessionStatus.Ended));
        command.Parameters.AddWithValue("$end_reason", EnumCodec.ToDb(reason));
        command.Parameters.AddWithValue("$id", sessionId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Session>> GetOpenSessionsAsync(CancellationToken ct = default)
    {
        await using var connection = _factory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, started_at, ended_at, status, end_reason, idle_threshold_s
            FROM sessions
            WHERE ended_at IS NULL
            ORDER BY started_at, id;
            """;
        return await ReadSessionsAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<Session?> GetLastSessionAsync(CancellationToken ct = default)
    {
        await using var connection = _factory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, started_at, ended_at, status, end_reason, idle_threshold_s
            FROM sessions
            ORDER BY id DESC
            LIMIT 1;
            """;
        var sessions = await ReadSessionsAsync(command, ct).ConfigureAwait(false);
        return sessions.Count > 0 ? sessions[0] : null;
    }

    // ---------------- События ----------------

    public async Task AppendEventAsync(Event ev, CancellationToken ct = default)
    {
        await using var connection = _factory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO events (session_id, ts, kind, application_id, project_id, title, path, url, meta_json)
            VALUES ($session_id, $ts, $kind, $application_id, $project_id, $title, $path, $url, $meta_json);
            """;
        command.Parameters.AddWithValue("$session_id", ev.SessionId);
        command.Parameters.AddWithValue("$ts", ev.Ts.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$kind", EnumCodec.ToDb(ev.Kind));
        command.Parameters.AddWithValue("$application_id", (object?)ev.ApplicationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$project_id", (object?)ev.ProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", (object?)ev.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("$path", (object?)ev.Path ?? DBNull.Value);
        command.Parameters.AddWithValue("$url", (object?)ev.Url ?? DBNull.Value);
        command.Parameters.AddWithValue("$meta_json", (object?)ev.MetaJson ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Event>> GetSessionEventsAsync(long sessionId, CancellationToken ct = default)
    {
        await using var connection = _factory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, session_id, ts, kind, application_id, project_id, title, path, url, meta_json
            FROM events
            WHERE session_id = $session_id
            ORDER BY ts, id;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);

        var result = new List<Event>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new Event(
                Id: reader.GetInt64(0),
                SessionId: reader.GetInt64(1),
                Ts: DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
                Kind: EnumCodec.FromDb<EventKind>(reader.GetString(3)),
                ApplicationId: reader.IsDBNull(4) ? null : reader.GetInt64(4),
                ProjectId: reader.IsDBNull(5) ? null : reader.GetInt64(5),
                Title: reader.IsDBNull(6) ? null : reader.GetString(6),
                Path: reader.IsDBNull(7) ? null : reader.GetString(7),
                Url: reader.IsDBNull(8) ? null : reader.GetString(8),
                MetaJson: reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return result;
    }

    // ---------------- Активность файлов ----------------

    public async Task AppendFileActivityAsync(FileActivity activity, CancellationToken ct = default)
    {
        await using var connection = _factory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO file_activity (session_id, ts, project_id, path, change_kind, source)
            VALUES ($session_id, $ts, $project_id, $path, $change_kind, $source);
            """;
        command.Parameters.AddWithValue("$session_id", activity.SessionId);
        command.Parameters.AddWithValue("$ts", activity.Ts.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$project_id", (object?)activity.ProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$path", activity.Path);
        command.Parameters.AddWithValue("$change_kind", EnumCodec.ToDb(activity.ChangeKind));
        command.Parameters.AddWithValue("$source", EnumCodec.ToDb(activity.Source));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ---------------- Проекты и приложения ----------------

    public async Task<Project?> FindProjectByRootPathAsync(string rootPath, CancellationToken ct = default)
    {
        await using var connection = _factory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, root_path, git_remote, first_seen, last_seen
            FROM projects
            WHERE root_path = $root_path;
            """;
        command.Parameters.AddWithValue("$root_path", rootPath);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return MapProject(reader);
    }

    public async Task<long> UpsertProjectAsync(Project project, CancellationToken ct = default)
    {
        await using var connection = _factory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO projects (name, root_path, git_remote, first_seen, last_seen)
            VALUES ($name, $root_path, $git_remote, $seen, $seen)
            ON CONFLICT(root_path) DO UPDATE SET
                name = excluded.name,
                git_remote = COALESCE(excluded.git_remote, projects.git_remote),
                last_seen = excluded.last_seen
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$name", project.Name);
        command.Parameters.AddWithValue("$root_path", project.RootPath);
        command.Parameters.AddWithValue("$git_remote", (object?)project.GitRemote ?? DBNull.Value);
        command.Parameters.AddWithValue("$seen", _clock.UtcNow.ToUnixTimeSeconds());
        return (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    public async Task<long> UpsertApplicationAsync(ApplicationInfo application, CancellationToken ct = default)
    {
        await using var connection = _factory.OpenConnection();
        await using var command = connection.CreateCommand();
        // exe_path в БД — NOT NULL DEFAULT '': уникальность (name, exe_path)
        // работает, потому что SQLite считает NULL различными
        command.CommandText = """
            INSERT INTO applications (name, exe_path, adapter_key, first_seen, last_seen)
            VALUES ($name, $exe_path, $adapter_key, $seen, $seen)
            ON CONFLICT(name, exe_path) DO UPDATE SET
                adapter_key = COALESCE(excluded.adapter_key, applications.adapter_key),
                last_seen = excluded.last_seen
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$name", application.Name);
        command.Parameters.AddWithValue("$exe_path", application.ExePath ?? string.Empty);
        command.Parameters.AddWithValue("$adapter_key", (object?)application.AdapterKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$seen", _clock.UtcNow.ToUnixTimeSeconds());
        return (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    // ---------------- Снапшоты ----------------

    public async Task<long> SaveSnapshotAsync(Snapshot snapshot, CancellationToken ct = default)
    {
        await using var connection = _factory.OpenConnection();
        using var transaction = connection.BeginTransaction();

        long snapshotId;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO snapshots (session_id, ts, reason, schema_version, summary_json)
                VALUES ($session_id, $ts, $reason, $schema_version, $summary_json);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$session_id", snapshot.SessionId);
            command.Parameters.AddWithValue("$ts", snapshot.Ts.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$reason", EnumCodec.ToDb(snapshot.Reason));
            command.Parameters.AddWithValue("$schema_version", snapshot.SchemaVersion);
            command.Parameters.AddWithValue("$summary_json", snapshot.SummaryJson);
            snapshotId = (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        foreach (var git in snapshot.GitStates)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO git_state (snapshot_id, project_id, available, branch, head_commit,
                                       head_subject, head_ts, dirty_count, dirty_files_json, untracked_count)
                VALUES ($snapshot_id, $project_id, $available, $branch, $head_commit,
                        $head_subject, $head_ts, $dirty_count, $dirty_files_json, $untracked_count);
                """;
            command.Parameters.AddWithValue("$snapshot_id", snapshotId);
            command.Parameters.AddWithValue("$project_id", git.ProjectId);
            command.Parameters.AddWithValue("$available", git.Available ? 1 : 0);
            command.Parameters.AddWithValue("$branch", (object?)git.Branch ?? DBNull.Value);
            command.Parameters.AddWithValue("$head_commit", (object?)git.HeadCommit ?? DBNull.Value);
            command.Parameters.AddWithValue("$head_subject", (object?)git.HeadSubject ?? DBNull.Value);
            command.Parameters.AddWithValue("$head_ts", (object?)git.HeadTs?.ToUnixTimeSeconds() ?? DBNull.Value);
            command.Parameters.AddWithValue("$dirty_count", git.DirtyCount);
            command.Parameters.AddWithValue("$dirty_files_json", (object?)git.DirtyFilesJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$untracked_count", git.UntrackedCount);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var terminal in snapshot.Terminals)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO terminals (snapshot_id, available, host, shell, cwd, source)
                VALUES ($snapshot_id, $available, $host, $shell, $cwd, $source);
                """;
            command.Parameters.AddWithValue("$snapshot_id", snapshotId);
            command.Parameters.AddWithValue("$available", terminal.Available ? 1 : 0);
            command.Parameters.AddWithValue("$host", terminal.Host);
            command.Parameters.AddWithValue("$shell", (object?)terminal.Shell ?? DBNull.Value);
            command.Parameters.AddWithValue("$cwd", (object?)terminal.Cwd ?? DBNull.Value);
            command.Parameters.AddWithValue("$source", EnumCodec.ToDb(terminal.Source));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var file in snapshot.OpenFiles)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO open_files (snapshot_id, path, project_id, application_id, line, last_modified, source)
                VALUES ($snapshot_id, $path, $project_id, $application_id, $line, $last_modified, $source);
                """;
            command.Parameters.AddWithValue("$snapshot_id", snapshotId);
            command.Parameters.AddWithValue("$path", file.Path);
            command.Parameters.AddWithValue("$project_id", (object?)file.ProjectId ?? DBNull.Value);
            command.Parameters.AddWithValue("$application_id", (object?)file.ApplicationId ?? DBNull.Value);
            command.Parameters.AddWithValue("$line", (object?)file.Line ?? DBNull.Value);
            command.Parameters.AddWithValue("$last_modified", (object?)file.LastModified?.ToUnixTimeSeconds() ?? DBNull.Value);
            command.Parameters.AddWithValue("$source", file.Source);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var tab in snapshot.BrowserTabs)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO browser_tabs (snapshot_id, application_id, url, title, url_available, is_active, source)
                VALUES ($snapshot_id, $application_id, $url, $title, $url_available, $is_active, $source);
                """;
            command.Parameters.AddWithValue("$snapshot_id", snapshotId);
            command.Parameters.AddWithValue("$application_id", tab.ApplicationId);
            command.Parameters.AddWithValue("$url", (object?)tab.Url ?? DBNull.Value);
            command.Parameters.AddWithValue("$title", (object?)tab.Title ?? DBNull.Value);
            command.Parameters.AddWithValue("$url_available", tab.UrlAvailable ? 1 : 0);
            command.Parameters.AddWithValue("$is_active", tab.IsActive ? 1 : 0);
            command.Parameters.AddWithValue("$source", EnumCodec.ToDb(tab.Source));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        transaction.Commit();
        return snapshotId;
    }

    // ---------------- Настройки ----------------

    public async Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
    {
        await using var connection = _factory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value as string;
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken ct = default)
    {
        await using var connection = _factory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings (key, value, updated_at)
            VALUES ($key, $value, $updated_at)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$updated_at", _clock.UtcNow.ToUnixTimeSeconds());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ---------------- Маппинг ----------------

    private static async Task<IReadOnlyList<Session>> ReadSessionsAsync(SqliteCommand command, CancellationToken ct)
    {
        var result = new List<Session>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new Session(
                Id: reader.GetInt64(0),
                StartedAt: DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)),
                EndedAt: reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
                Status: EnumCodec.FromDb<SessionStatus>(reader.GetString(3)),
                EndReason: reader.IsDBNull(4) ? null : EnumCodec.FromDb<SessionEndReason>(reader.GetString(4)),
                IdleThresholdSeconds: reader.GetInt32(5)));
        }

        return result;
    }

    private static Project MapProject(SqliteDataReader reader) => new(
        Id: reader.GetInt64(0),
        Name: reader.GetString(1),
        RootPath: reader.GetString(2),
        GitRemote: reader.IsDBNull(3) ? null : reader.GetString(3),
        FirstSeen: DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)),
        LastSeen: DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)));
}
