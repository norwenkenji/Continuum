using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Continuum.Tests.Database;

/// <summary>
/// Схема БД через миграции Entity Framework Core: все таблицы предметной
/// области создаются, журнал миграций ведётся в __EFMigrationsHistory,
/// повторный запуск идемпотентен, БД до перехода на EF (схема без журнала)
/// подхватывается с сохранением данных.
/// </summary>
public sealed class EfSchemaTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "continuum-ef-schema", Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_dir, "test.db");

    private Infrastructure.Database.SqliteConnectionFactory Factory => new(DbPath);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // уборка - best effort
        }
    }

    private static List<string> ReadNames(string dbPath, string type) =>
        Query(dbPath, $"SELECT name FROM sqlite_master WHERE type = '{type}'");

    private static List<string> Query(string dbPath, string sql)
    {
        var result = new List<string>();
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static void Execute(string dbPath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Migrations_create_all_domain_tables()
    {
        new Infrastructure.Database.DatabaseInitializer(Factory).Initialize();

        var tables = ReadNames(DbPath, "table");
        foreach (var expected in new[]
                 {
                     "sessions", "events", "file_activity", "projects", "applications",
                     "snapshots", "git_state", "terminals", "open_files", "browser_tabs",
                     "restore_plans", "restore_steps", "settings", "__EFMigrationsHistory",
                 })
        {
            Assert.Contains(expected, tables);
        }
    }

    [Fact]
    public void Migrations_create_unique_indexes_and_foreign_keys()
    {
        new Infrastructure.Database.DatabaseInitializer(Factory).Initialize();

        var indexes = ReadNames(DbPath, "index");
        Assert.Contains("ux_applications_name_exe", indexes);
        Assert.Contains("ix_events_session_ts", indexes);
        Assert.Contains("ix_events_ts", indexes);
        Assert.Contains("ix_fileact_session_ts", indexes);
        Assert.Contains("ix_fileact_path", indexes);
        Assert.Contains("ix_snapshots_session", indexes);
        Assert.Contains("ix_restore_steps_plan", indexes);

        // Ссылочная целостность: вставка события с несуществующей сессией запрещена
        var error = Record.Exception(() => Execute(
            DbPath,
            "INSERT INTO events (session_id, ts, kind) VALUES (999, 1, 'session_start');"));
        Assert.NotNull(error);
    }

    [Fact]
    public void Second_initialize_is_idempotent()
    {
        var initializer = new Infrastructure.Database.DatabaseInitializer(Factory);
        initializer.Initialize();
        initializer.Initialize();

        var historyRows = Query(DbPath, "SELECT MigrationId FROM __EFMigrationsHistory");
        Assert.Single(historyRows);
    }

    [Fact]
    public void Legacy_database_without_history_is_baselined_without_data_loss()
    {
        // БД, созданная до перехода на EF: схема есть, журнала миграций нет
        var initializer = new Infrastructure.Database.DatabaseInitializer(Factory);
        initializer.Initialize();
        Execute(DbPath, "DROP TABLE __EFMigrationsHistory;");
        Execute(DbPath, "INSERT INTO sessions (started_at, status, idle_threshold_s) VALUES (100, 'ended', 900);");

        initializer.Initialize();

        Assert.Single(Query(DbPath, "SELECT MigrationId FROM __EFMigrationsHistory"));
        Assert.Single(Query(DbPath, "SELECT id FROM sessions"));
    }
}
