using System;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Interfaces;
using Continuum.Settings;
using Microsoft.Extensions.Logging;

namespace Continuum.Maintenance;

/// <summary>
/// Срок хранения данных (ФТ-7.2, §4.6 спецификации privacy): сессии,
/// завершённые раньше настроенного срока, удаляются вместе с дочерними
/// данными. Проверка выполняется при старте, по таймеру (раз в 6 часов)
/// и сразу после сохранения настроек - новый срок действует без перезапуска.
/// 0 дней означает «хранить бессрочно». Никогда не бросает.
/// </summary>
public sealed class RetentionService : IDisposable
{
    /// <summary>Период фоновой проверки срока хранения.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(6);

    private readonly IRepository _repository;
    private readonly SettingsService _settings;
    private readonly IClock _clock;
    private readonly ILogger<RetentionService> _logger;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public RetentionService(
        IRepository repository,
        SettingsService settings,
        IClock clock,
        ILogger<RetentionService> logger,
        TimeSpan? interval = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _interval = interval ?? DefaultInterval;

        // Сохранение нового срока действует сразу, без ожидания таймера
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>
    /// Удаляет сессии старше настроенного срока. Возвращает число удалённых
    /// сессий; 0 - хранение бессрочное или удалять нечего.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var period = _settings.Settings.RetentionPeriod;
        if (period is null)
        {
            return 0;
        }

        await _runLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var threshold = _clock.UtcNow - period.Value;
            var deleted = await _repository.DeleteSessionsOlderThanAsync(threshold, ct).ConfigureAwait(false);
            if (deleted > 0)
            {
                _logger.LogInformation(
                    "Срок хранения {Days} дн.: удалено сессий - {Count} (порог {Threshold:u})",
                    _settings.Settings.RetentionDays, deleted, threshold);
            }

            return deleted;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            // Обслуживание данных не должно ронять приложение
            _logger.LogWarning(ex, "Не удалось применить срок хранения");
            return 0;
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>Запускает фоновую проверку по таймеру. Идемпотентно.</summary>
    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        var cts = _cts;
        var loop = _loop;
        _cts = null;
        _loop = null;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        try
        {
            loop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Фоновая задача не должна ронять остановку
        }

        cts.Dispose();
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        Stop();
        _runLock.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await RunOnceAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // штатная остановка
        }
    }

    private void OnSettingsChanged(object? sender, Core.Domain.AppSettings settings)
    {
        // Настройки сохранили - применяем срок сразу, не дожидаясь таймера
        _ = Task.Run(async () =>
        {
            try
            {
                await RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // RunOnceAsync сам логирует; здесь только страховка от падения таска
            }
        });
    }
}
