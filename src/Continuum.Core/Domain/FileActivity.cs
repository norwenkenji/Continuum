namespace Continuum.Core.Domain;

/// <summary>
/// Факт активности с файлом в рамках сессии (file_activity).
/// Source обязателен: показывает, откуда факт, и определяет степень доверия.
/// </summary>
public sealed record FileActivity(
    long Id,
    long SessionId,
    DateTimeOffset Ts,
    long? ProjectId,
    string Path,
    FileChangeKind ChangeKind,
    FileActivitySource Source);
