using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Infrastructure.Database;
using Continuum.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Continuum.Maintenance;

/// <summary>
/// Полное удаление данных (ФТ-7.2, §4.6): файл базы стирается вместе со
/// служебными файлами -wal и -shm и создаётся заново с чистой схемой
/// (миграции EF Core). Пул соединений очищается перед удалением - иначе
/// SQLite держит файл открытым. После вайпа настройки перечитываются
/// (они жили в базе и вернулись к умолчаниям).
/// Сессию останавливает и запускает вызывающий (см. MaintenanceCoordinator):
/// удалять базу под работающим конвейером нельзя.
/// </summary>
public sealed class DataWipeService
{
    private readonly SqliteConnectionFactory _connections;
    private readonly DatabaseInitializer _initializer;
    private readonly SettingsService _settings;
    private readonly ILogger<DataWipeService> _logger;

    public DataWipeService(
        SqliteConnectionFactory connections,
        DatabaseInitializer initializer,
        SettingsService settings,
        ILogger<DataWipeService> logger)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Стирает базу и создаёт её заново. Возвращает список удалённых файлов.
    /// </summary>
    public async Task<IReadOnlyList<string>> WipeAsync(CancellationToken ct = default)
    {
        var deleted = new List<string>();

        await Task.Run(
            () =>
            {
                // Соединения пула держат файл - иначе File.Delete упадёт
                SqliteConnection.ClearAllPools();

                var databasePath = _connections.DatabasePath;
                if (!string.Equals(databasePath, ":memory:", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
                    {
                        var path = databasePath + suffix;
                        if (!File.Exists(path))
                        {
                            continue;
                        }

                        File.Delete(path);
                        deleted.Add(path);
                    }
                }

                // Чистая схема - миграциями EF Core, как при первом запуске
                _initializer.Initialize();
            },
            ct).ConfigureAwait(false);

        // Настройки жили в базе: перечитываем умолчания и применяем к исключениям
        await _settings.LoadAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Данные удалены полностью: файлов {Count}", deleted.Count);
        return deleted;
    }
}
