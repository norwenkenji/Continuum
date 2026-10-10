using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Continuum.Runtime;

/// <summary>
/// Полные границы сессии (readme «Границы сессии», шаг 4):
/// - idle: нет ввода дольше порога -> снапшот (session_end) + закрытие сессии
///   с причиной idle; возврат ввода -> новая сессия;
/// - sleep/wake системы: события system_sleep/system_wake (сессия не рвётся -
///   при пробуждении сработает обычный idle-порог, если сон был долгим).
///
/// Тик - раз в PollInterval (по умолчанию 30 с). Не IObservationSource:
/// события пишет сам через IRepository (это не наблюдения, а границы сессии).
/// </summary>
public sealed class SessionSupervisor : IDisposable
{
    /// <summary>Период проверки idle.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(30);

    private readonly SessionRuntime _runtime;
    private readonly IRepository _repository;
    private readonly IIdleTimeSource _idle;
    private readonly ISnapshotter _snapshotter;
    private readonly IClock _clock;
    private readonly ILogger<SessionSupervisor> _logger;
    private readonly TimeSpan _idleThreshold;
    private readonly TimeSpan _pollInterval;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public SessionSupervisor(
        SessionRuntime runtime,
        IRepository repository,
        IIdleTimeSource idle,
        ISnapshotter snapshotter,
        IClock clock,
        ILogger<SessionSupervisor> logger,
        TimeSpan? idleThreshold = null,
        TimeSpan? pollInterval = null)
    {
        _runtime = runtime;
        _repository = repository;
        _idle = idle;
        _snapshotter = snapshotter;
        _clock = clock;
        _logger = logger;
        _idleThreshold = idleThreshold ?? TimeSpan.FromSeconds(SessionRuntime.DefaultIdleThresholdSeconds);
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Подписка на sleep/wake - только при живом UI (тесты зовут HandlePowerMode напрямую).</summary>
    public void HookPowerEvents()
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public void Stop()
    {
        try
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
        catch (InvalidOperationException)
        {
            // Нет ни одного подписчика SystemEvents (тесты без HookPowerEvents) - не ошибка
        }

        _cts?.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // выход приложения - не мешаем
        }

        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    public void Dispose() => Stop();

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await TickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Сбой тика SessionSupervisor - продолжаю");
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var idleFor = _idle.GetIdleTime();
        var sessionId = _runtime.CurrentSessionId;

        if (sessionId != 0 && idleFor >= _idleThreshold)
        {
            _logger.LogInformation("Простой {Idle} >= порога {Threshold} - закрываю сессию по idle",
                idleFor, _idleThreshold);
            // Снапшот на границе сессии - до закрытия, пока контекст текущий
            await _snapshotter.SaveSnapshotAsync(SnapshotReason.SessionEnd, ct).ConfigureAwait(false);
            await _runtime.StopAsync(SessionEndReason.Idle, ct).ConfigureAwait(false);
            return;
        }

        if (sessionId == 0 && idleFor < _idleThreshold)
        {
            // Ввод вернулся после idle-закрытия - новая сессия (readme: START)
            _logger.LogInformation("Ввод вернулся после простоя - открываю новую сессию");
            await _runtime.StartAsync(ct).ConfigureAwait(false);
        }
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        => HandlePowerMode(e.Mode);

    /// <summary>Реакция на sleep/wake; public - шов для тестов (SystemEvents не поднять без рабочего стола).</summary>
    public void HandlePowerMode(Microsoft.Win32.PowerModes mode)
    {
        var kind = mode switch
        {
            Microsoft.Win32.PowerModes.Suspend => EventKind.SystemSleep,
            Microsoft.Win32.PowerModes.Resume => EventKind.SystemWake,
            _ => (EventKind?)null,
        };
        if (kind is null)
        {
            return;
        }

        try
        {
            var sessionId = _runtime.CurrentSessionId;
            if (sessionId != 0)
            {
                _repository.AppendEventAsync(
                    new Event(0, sessionId, _clock.UtcNow, kind.Value, null, null, null, null, null, null))
                    .GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не записалось событие {Kind}", kind.Value);
        }
    }
}
