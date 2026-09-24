using System.Reflection;

namespace Continuum.Infrastructure.Database;

/// <summary>
/// Приводит БД к актуальной версии схемы. Вызывается при старте приложения
/// до показа главного окна: хранилище создаётся само при первом запуске
/// (zero-setup), миграции идемпотентны.
/// </summary>
public sealed class DatabaseInitializer
{
    private readonly SqliteConnectionFactory _factory;
    private readonly Assembly _migrationsAssembly;

    public DatabaseInitializer(SqliteConnectionFactory factory)
        : this(factory, typeof(DatabaseInitializer).Assembly)
    {
    }

    /// <summary>Конструктор для тестов: миграции можно искать в другой сборке.</summary>
    public DatabaseInitializer(SqliteConnectionFactory factory, Assembly migrationsAssembly)
    {
        _factory = factory;
        _migrationsAssembly = migrationsAssembly;
    }

    /// <summary>Применяет ожидающие миграции. Возвращает версии применённых.</summary>
    public System.Collections.Generic.IReadOnlyList<int> Initialize()
    {
        using var connection = _factory.OpenConnection();
        var migrations = EmbeddedMigrationLoader.Load(_migrationsAssembly);
        return new MigrationRunner().Migrate(connection, migrations);
    }
}
