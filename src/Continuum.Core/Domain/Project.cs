namespace Continuum.Core.Domain;

/// <summary>
/// Рабочая единица Context Engine. Первичный ключ идентификации —
/// нормализованный RootPath; GitRemote — дополнительный признак, не ключ.
/// </summary>
public sealed record Project(
    long Id,
    string Name,
    string RootPath,
    string? GitRemote,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);
