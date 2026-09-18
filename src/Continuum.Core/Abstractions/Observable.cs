namespace Continuum.Core.Abstractions;

/// <summary>
/// Единица наблюдения до обработки privacy-конвейером.
/// Контракт специфицирован в docs/specs/privacy-pipeline.md, §2.
/// </summary>
public sealed record Observable(
    ObservableKind Kind,
    string? Value,
    int? ProcessId,
    string? ApplicationName,
    string? ApplicationExePath,
    DateTimeOffset Timestamp,
    string? ProjectRootPath);
