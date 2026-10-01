using System;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Continuum.Runtime;

/// <summary>
/// Жизненный цикл сессии работы. Шаг 2 даёт минимум: одна сессия на запуск
/// приложения, закрытие висячих сессий прошлых запусков (crash-recovery),
/// события session_start/session_end. Полные границы сессии (idle-порог,
/// system sleep/wake) - шаг 4 дорожной карты.
/// </summary>
public sealed class SessionRuntime : ISessionContext
{
    /// <summary>Порог простоя по умолчанию: 15 минут (readme, «Границы сессии»).</summary>
    public const int DefaultIdleThresholdSeconds = 15 * 60;

    private readonly IRepository _repository;
    private readonly IClock _clock;
    private readonly ILogger<SessionRuntime> _logger;
    private long _currentSessionId;

    public SessionRuntime(IRepository repository, IClock clock, ILogger<SessionRuntime> logger)
    {
        _repository = repository;
        _clock = clock;
        _logger = logger;
    }

    public long CurrentSessionId => Interlocked.Read(ref _currentSessionId);

    /// <summary>
    /// Закрывает висячие сессии прошлых запусков (crash_recovered),
    /// создаёт новую сессию и пишет событие session_start.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        var open = await _repository.GetOpenSessionsAsync(ct).ConfigureAwait(false);
        foreach (var stale in open)
        {
            _logger.LogWarning("Закрываю висячую сессию {SessionId} от {StartedAt}", stale.Id, stale.StartedAt);
            await _repository.EndSessionAsync(stale.Id, _clock.UtcNow, SessionEndReason.CrashRecovered, ct)
                .ConfigureAwait(false);
        }

        var now = _clock.UtcNow;
        var sessionId = await _repository.CreateSessionAsync(
            new Session(0, now, null, SessionStatus.Running, null, DefaultIdleThresholdSeconds), ct)
            .ConfigureAwait(false);
        Interlocked.Exchange(ref _currentSessionId, sessionId);

        await _repository.AppendEventAsync(
            new Event(0, sessionId, now, EventKind.SessionStart, null, null, null, null, null, null), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Пишет session_end и закрывает текущую сессию. Повторный вызов - no-op,
    /// поэтому безопасен из нескольких путей выхода (трей, меню, shutdown).
    /// </summary>
    public async Task StopAsync(SessionEndReason reason, CancellationToken ct = default)
    {
        var sessionId = Interlocked.Exchange(ref _currentSessionId, 0);
        if (sessionId == 0)
        {
            return;
        }

        var now = _clock.UtcNow;
        await _repository.AppendEventAsync(
            new Event(0, sessionId, now, EventKind.SessionEnd, null, null, null, null, null, null), ct)
            .ConfigureAwait(false);
        await _repository.EndSessionAsync(sessionId, now, reason, ct).ConfigureAwait(false);
    }
}
