using Continuum.Infrastructure.Database;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Continuum.Tests.Database;

public class MigrationRunnerTests
{
    private static SqliteConnection OpenMemory()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    [Fact]
    public void Applies_pending_migrations_in_order_and_sets_user_version()
    {
        using var connection = OpenMemory();

        // Намеренно не по порядку: runner обязан отсортировать
        var migrations = new[]
        {
            new Migration(2, "second", "CREATE TABLE t2 (id INTEGER PRIMARY KEY);"),
            new Migration(1, "first", "CREATE TABLE t1 (id INTEGER PRIMARY KEY);"),
        };

        var applied = new MigrationRunner().Migrate(connection, migrations);

        Assert.Equal(new[] { 1, 2 }, applied);
        Assert.Equal(2, MigrationRunner.GetUserVersion(connection));

        using var check = connection.CreateCommand();
        check.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
        var tables = new System.Collections.Generic.List<string?>();
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        Assert.Equal(new[] { "t1", "t2" }, tables);
    }

    [Fact]
    public void Second_run_applies_nothing()
    {
        using var connection = OpenMemory();
        var migrations = new[] { new Migration(1, "first", "CREATE TABLE t1 (id INTEGER PRIMARY KEY);") };

        new MigrationRunner().Migrate(connection, migrations);
        var appliedAgain = new MigrationRunner().Migrate(connection, migrations);

        Assert.Empty(appliedAgain);
        Assert.Equal(1, MigrationRunner.GetUserVersion(connection));
    }

    [Fact]
    public void Duplicate_versions_are_rejected()
    {
        using var connection = OpenMemory();
        var migrations = new[]
        {
            new Migration(1, "a", "SELECT 1;"),
            new Migration(1, "b", "SELECT 2;"),
        };

        Assert.Throws<System.InvalidOperationException>(
            () => new MigrationRunner().Migrate(connection, migrations));
    }

    [Fact]
    public void Failed_migration_keeps_user_version_and_previous_tables()
    {
        using var connection = OpenMemory();
        var migrations = new[]
        {
            new Migration(1, "good", "CREATE TABLE t1 (id INTEGER PRIMARY KEY);"),
            new Migration(2, "broken", "CREATE TABLE broken SQL HERE;"),
        };

        Assert.Throws<SqliteException>(() => new MigrationRunner().Migrate(connection, migrations));

        // Первая миграция закоммичена и версия зафиксирована, вторая откатилась
        Assert.Equal(1, MigrationRunner.GetUserVersion(connection));

        using var check = connection.CreateCommand();
        check.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 't1';";
        Assert.Equal("t1", check.ExecuteScalar());
    }
}
