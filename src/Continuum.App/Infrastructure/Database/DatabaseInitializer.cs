using System;
using System.Linq;
using Continuum.Infrastructure.Database.Ef;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Continuum.Infrastructure.Database;

/// <summary>
/// Приводит БД к актуальной версии схемы. Вызывается при старте приложения
/// до показа главного окна: хранилище создаётся само при первом запуске
/// (zero-setup), схема создаётся и обновляется миграциями Entity Framework Core
/// (журнал применённых миграций - __EFMigrationsHistory).
/// </summary>
public sealed class DatabaseInitializer
{
    private readonly SqliteConnectionFactory _connections;

    public DatabaseInitializer(SqliteConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        _connections = connections;
    }

    /// <summary>Применяет неприменённые миграции EF Core.</summary>
    public void Initialize()
    {
        using var db = new ContinuumDbContext(_connections);
        BaselineLegacyDatabase(db);
        db.Database.Migrate();
    }

    /// <summary>
    /// Совместимость с БД, созданными до перехода проекта на EF Core
    /// (схема V001 без журнала миграций): журнал дописывается задним числом,
    /// данные не трогаются. Для чистой БД ничего не делает - Migrate создаст
    /// схему с нуля.
    /// </summary>
    private static void BaselineLegacyDatabase(ContinuumDbContext db)
    {
        if (db.Database.GetAppliedMigrations().Any())
        {
            return;
        }

        // Служебный запрос к каталогу SQLite: есть ли таблица предметной области
        var hasLegacySchema = db.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name = 'sessions'")
            .AsEnumerable()
            .First() > 0;
        if (!hasLegacySchema)
        {
            return;
        }

        var initialMigration = db.Database.GetMigrations().FirstOrDefault();
        if (initialMigration is null)
        {
            return;
        }

        // Скрипты истории миграций генерирует сам EF - схема журнала и строка
        // вставки совпадают с тем, что ожидает Migrate
        var history = db.GetService<IHistoryRepository>();
        db.Database.ExecuteSqlRaw(history.GetCreateIfNotExistsScript());
        db.Database.ExecuteSqlRaw(
            history.GetInsertScript(new HistoryRow(initialMigration, ProductInfo.GetVersion())));
    }
}
