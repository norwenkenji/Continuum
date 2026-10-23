namespace Continuum.Core.Domain;

/// <summary>
/// Ответ движка «Где я остановился?» (readme, одноимённый раздел):
/// вердикт по таблице истинности плюс факты, из которых он выведен.
/// Все факты nullable - при Unclear их может не быть вовсе.
/// </summary>
public sealed record StoppedVerdict(
    StoppedStatus Status,
    long? ProjectId,
    string? ProjectName,
    string? ProjectRoot,
    string? LastFilePath,
    string? Branch,
    string? HeadCommit,
    string? HeadSubject,
    DateTimeOffset? HeadTs,
    int DirtyCount);
