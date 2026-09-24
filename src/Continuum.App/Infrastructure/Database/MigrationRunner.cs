using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Continuum.Infrastructure.Database;

/// <summary>
/// Применяет миграции к SQLite. Версия схемы — PRAGMA user_version
/// (без EF, миграции пишутся SQL-скриптами вручную).
///
/// Правила:
/// - применяются только миграции с Version &gt; user_version, по возрастанию;
/// - каждая миграция — в своей транзакции;
/// - user_version обновляется ПОСЛЕ коммита транзакции миграции;
/// - дубликаты версий — ошибка конфигурации, не применяемых молча.
/// </summary>
public sealed class MigrationRunner
{
    /// <summary>Применяет ожидающие миграции. Возвращает версии применённых.</summary>
    public IReadOnlyList<int> Migrate(SqliteConnection connection, IReadOnlyList<Migration> migrations)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(migrations);

        var ordered = migrations.OrderBy(m => m.Version).ToArray();
        var duplicate = ordered.GroupBy(m => m.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Дублируется версия миграции: {duplicate.Key}.");
        }

        var current = GetUserVersion(connection);
        var applied = new List<int>();

        foreach (var migration in ordered)
        {
            if (migration.Version <= current)
            {
                continue;
            }

            using (var transaction = connection.BeginTransaction())
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = migration.Sql;
                command.ExecuteNonQuery();
                transaction.Commit();
            }

            SetUserVersion(connection, migration.Version);
            applied.Add(migration.Version);
        }

        return applied;
    }

    public static int GetUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void SetUserVersion(SqliteConnection connection, int version)
    {
        using var command = connection.CreateCommand();
        // version — int по контракту Migration, интерполяция безопасна
        command.CommandText = $"PRAGMA user_version = {version};";
        command.ExecuteNonQuery();
    }
}
