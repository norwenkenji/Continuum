namespace Continuum.Core.Domain;

/// <summary>
/// Атомарная запись хронологии (events). Append-only: событие пишется
/// на изменение состояния, не по таймеру, и не редактируется задним числом.
/// </summary>
public sealed record Event(
    long Id,
    long SessionId,
    DateTimeOffset Ts,
    EventKind Kind,
    long? ApplicationId,
    long? ProjectId,
    string? Title,
    string? Path,
    string? Url,
    string? MetaJson);
