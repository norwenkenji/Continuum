using Continuum.Infrastructure.Database;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Continuum.Tests.Database;

/// <summary>
/// Проверяет миграцию V001 на временной файловой БД через DatabaseInitializer
/// (конструктор по умолчанию): версия схемы, состав таблиц и индексов,
/// идемпотентность повторного запуска.
/// </summary>
public class MigrationV001Tests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(), "continuum-tests", Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_tempDirectory, "continuum.db");

    [Fact]
    public void Initialize_creates_13_tables_all_indexes_and_sets_user_version_1()
    {
        var initializer = new DatabaseInitializer(new SqliteConnectionFactory(DatabasePath));

        var applied = initializer.Initialize();

        Assert.Equal(new[] { 1 }, applied);

        using var connection = new SqliteConnectionFactory(DatabasePath).OpenConnection();
        Assert.Equal(1, MigrationRunner.GetUserVersion(connection));

        // Ровно 13 таблиц из схемы readme (служебные sqlite_* не считаем)
        var tables = QueryNames(connection, "table");
        Assert.Equal(
            new[]
            {
                "applications", "browser_tabs", "events", "file_activity", "git_state",
                "open_files", "projects", "restore_plans", "restore_steps", "sessions",
                "settings", "snapshots", "terminals",
            },
            tables);

        // Все индексы миграции: 6 из readme + ux_applications_name_exe
        // (автоиндексы sqlite_autoindex_* от UNIQUE-ограничений не считаем)
        var indexes = QueryNames(connection, "index");
        Assert.Equal(
            new[]
            {
                "ix_events_session_ts", "ix_events_ts", "ix_fileact_path",
                "ix_fileact_session_ts", "ix_restore_steps_plan", "ix_snapshots_session",
                "ux_applications_name_exe",
            },
            indexes);
    }

    [Fact]
    public void Initialize_is_idempotent()
    {
        var initializer = new DatabaseInitializer(new SqliteConnectionFactory(DatabasePath));

        initializer.Initialize();
        var secondRun = initializer.Initialize(); // повторный запуск - без ошибок

        Assert.Empty(secondRun);

        using var connection = new SqliteConnectionFactory(DatabasePath).OpenConnection();
        Assert.Equal(1, MigrationRunner.GetUserVersion(connection));
    }

    [Fact]
    public void Applications_name_exe_path_unique_index_supports_upsert()
    {
        var initializer = new DatabaseInitializer(new SqliteConnectionFactory(DatabasePath));
        initializer.Initialize();

        using var connection = new SqliteConnectionFactory(DatabasePath).OpenConnection();

        // Повтор (name, exe_path) обязан падать по ux_applications_name_exe -
        // именно на него опирается ON CONFLICT(name, exe_path) в UpsertApplicationAsync
        InsertApplication(connection, "code", "C:\\Apps\\code.exe");
        Assert.Throws<SqliteException>(() => InsertApplication(connection, "code", "C:\\Apps\\code.exe"));

        // Дубли name с другим exe_path допустимы; пустой exe_path ведёт себя
        // как обычное значение (именно это ломалось бы с nullable exe_path)
        InsertApplication(connection, "code", "");
        Assert.Throws<SqliteException>(() => InsertApplication(connection, "code", ""));
    }

    private static void InsertApplication(SqliteConnection connection, string name, string exePath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO applications (name, exe_path) VALUES ($name, $exe_path);";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$exe_path", exePath);
        command.ExecuteNonQuery();
    }

    private static string[] QueryNames(SqliteConnection connection, string type)
    {
        using var command = connection.CreateCommand();
        // type - внутренняя константа теста ("table"/"index"), интерполяция безопасна
        command.CommandText =
            $"SELECT name FROM sqlite_master WHERE type = '{type}' AND name NOT LIKE 'sqlite_%' ORDER BY name;";

        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return [.. names];
    }

    public void Dispose()
    {
        // Соединения пулятся: без очистки пула файл БД остаётся залоченным
        SqliteConnection.ClearAllPools();

        // Удаляем временную БД вместе с -wal/-shm
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
