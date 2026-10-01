namespace Continuum.Core.Domain;

/// <summary>
/// Период непрерывной работы. Времена хранятся в БД как unix-секунды UTC,
/// в домене - DateTimeOffset. Границы сессии: см. readme «Схема БД → Границы сессии».
/// </summary>
public sealed record Session(
    long Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    SessionStatus Status,
    SessionEndReason? EndReason,
    int IdleThresholdSeconds);
