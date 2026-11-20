using System;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Domain;
using Continuum.Runtime;
using Continuum.Snapshots;
using Microsoft.Extensions.Logging;

namespace Continuum.Maintenance;

/// <summary>Полное удаление данных с согласованием сессии - шов для моделей представления.</summary>
public interface IDataWipeCoordinator
{
    /// <summary>Стирает данные и возвращает приложение в рабочее состояние.</summary>
    Task WipeAllDataAsync(CancellationToken ct = default);
}

/// <summary>
/// Согласование обслуживания данных с работающим приложением: полное
/// удаление выполняется при остановленной сессии и остановленных таймерах,
/// после вайпа сессия и таймеры запускаются заново - приложение продолжает
/// работать на чистой базе без перезапуска (требование ФТ-7.2 в трактовке
/// «удаление по кнопке, программа жива»).
/// </summary>
public sealed class MaintenanceCoordinator : IDataWipeCoordinator
{
    private readonly SessionRuntime _session;
    private readonly SessionSupervisor _supervisor;
    private readonly Snapshotter _snapshotter;
    private readonly DataWipeService _wipe;
    private readonly ILogger<MaintenanceCoordinator> _logger;

    public MaintenanceCoordinator(
        SessionRuntime session,
        SessionSupervisor supervisor,
        Snapshotter snapshotter,
        DataWipeService wipe,
        ILogger<MaintenanceCoordinator> logger)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _snapshotter = snapshotter ?? throw new ArgumentNullException(nameof(snapshotter));
        _wipe = wipe ?? throw new ArgumentNullException(nameof(wipe));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Данные стёрты и приложение снова пишет в чистую базу.</summary>
    public event EventHandler? DataWiped;

    /// <summary>Полное удаление данных с перезапуском сессии. Никогда не бросает.</summary>
    public async Task WipeAllDataAsync(CancellationToken ct = default)
    {
        try
        {
            // 1. Таймеры и сессия - стоп: под работающим конвейером базу не удаляем
            TryStopSupervisor();
            _snapshotter.Stop();
            await _session.StopAsync(SessionEndReason.User, ct).ConfigureAwait(false);

            // 2. Файлы базы + чистая схема + настройки по умолчанию
            await _wipe.WipeAsync(ct).ConfigureAwait(false);

            // 3. Сессия и таймеры - снова в работу
            await _session.StartAsync(ct).ConfigureAwait(false);
            _supervisor.Start();
            _snapshotter.Start();

            DataWiped?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Полное удаление данных не удалось");
        }
    }

    private void TryStopSupervisor()
    {
        try
        {
            _supervisor.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Остановка супервизора сессии не удалась");
        }
    }
}
