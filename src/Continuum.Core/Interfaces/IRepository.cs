using Continuum.Core.Domain;

namespace Continuum.Core.Interfaces;

/// <summary>
/// Хранилище Continuum. Единственная реализация - SQLite
/// (Continuum.App/Infrastructure/Database/SqliteRepository).
/// Времена - DateTimeOffset UTC; в БД хранятся unix-секунды.
/// Строковые ключи (root_path, name) должны приходить уже нормализованными:
/// сравнение в БД точное, нормализация - забота вызывающего.
/// </summary>
public interface IRepository
{
    // Сессии

    /// <summary>Создаёт сессию, возвращает её id. Id и EndedAt входной записи игнорируются.</summary>
    Task<long> CreateSessionAsync(Session session, CancellationToken ct = default);

    /// <summary>Закрывает сессию: ended_at, status = ended, end_reason.</summary>
    Task EndSessionAsync(long sessionId, DateTimeOffset endedAt, SessionEndReason reason, CancellationToken ct = default);

    /// <summary>Висячие сессии (ended_at IS NULL) - для crash-recovery при старте.</summary>
    Task<IReadOnlyList<Session>> GetOpenSessionsAsync(CancellationToken ct = default);

    /// <summary>Последняя по времени сессия (для главного окна).</summary>
    Task<Session?> GetLastSessionAsync(CancellationToken ct = default);

    // События (append-only)

    Task AppendEventAsync(Event ev, CancellationToken ct = default);

    /// <summary>События сессии по возрастанию времени - хронология.</summary>
    Task<IReadOnlyList<Event>> GetSessionEventsAsync(long sessionId, CancellationToken ct = default);

    /// <summary>Время последнего события сессии; null - событий нет (crash-recovery).</summary>
    Task<DateTimeOffset?> GetLastEventTsAsync(long sessionId, CancellationToken ct = default);

    // Активность файлов

    Task AppendFileActivityAsync(FileActivity activity, CancellationToken ct = default);

    // Проекты и приложения

    Task<Project?> FindProjectByRootPathAsync(string rootPath, CancellationToken ct = default);

    /// <summary>Вставка или обновление по RootPath; при конфликте обновляет name и last_seen. Возвращает id.</summary>
    Task<long> UpsertProjectAsync(Project project, CancellationToken ct = default);

    /// <summary>Вставка или обновление по (Name, ExePath). Возвращает id.</summary>
    Task<long> UpsertApplicationAsync(ApplicationInfo application, CancellationToken ct = default);

    // Снапшоты

    /// <summary>Сохраняет снапшот со всеми дочерними наборами в одной транзакции. Возвращает id снапшота.</summary>
    Task<long> SaveSnapshotAsync(Snapshot snapshot, CancellationToken ct = default);

    // Настройки

    Task<string?> GetSettingAsync(string key, CancellationToken ct = default);

    Task SetSettingAsync(string key, string value, CancellationToken ct = default);
}
