using System;
using System.IO;
using Continuum.Infrastructure.Database;
using Xunit;

namespace Continuum.Tests.Database;

public class SqliteConnectionFactoryTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(), "continuum-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void OpenConnection_creates_directory_and_sets_pragmas()
    {
        var databasePath = Path.Combine(_tempDirectory, "nested", "test.db");
        var factory = new SqliteConnectionFactory(databasePath);

        using var connection = factory.OpenConnection();

        Assert.True(File.Exists(databasePath));

        using var journal = connection.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string?)journal.ExecuteScalar());

        using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys;";
        Assert.Equal(1L, foreignKeys.ExecuteScalar());
    }

    [Fact]
    public void OpenConnection_supports_memory_database()
    {
        var factory = new SqliteConnectionFactory(":memory:");
        using var connection = factory.OpenConnection();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1;";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    public void Dispose()
    {
        // Соединения пулятся: без очистки пула файловый дескриптор остаётся
        // открытым после Dispose соединения, и каталог не удалить
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // WAL-файлы (-wal/-shm) тоже удаляем
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
