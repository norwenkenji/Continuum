using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Continuum.Infrastructure.Database;

/// <summary>
/// Открывает соединения с SQLite с едиными настройками:
/// WAL (конкурентное чтение во время записи), foreign_keys (ссылочная
/// целостность), synchronous=NORMAL (быстрее и достаточно в WAL - важно
/// в окне WM_QUERYENDSESSION, где на запись есть ~5 секунд).
/// Каталог БД создаётся автоматически - zero-setup.
/// </summary>
public sealed class SqliteConnectionFactory
{
    public SqliteConnectionFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = databasePath;
    }

    /// <summary>Полный путь к файлу БД или ":memory:".</summary>
    public string DatabasePath { get; }

    public SqliteConnection OpenConnection()
    {
        if (!string.Equals(DatabasePath, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(DatabasePath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText =
                "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
        }

        return connection;
    }
}
