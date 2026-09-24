namespace Continuum.Core.Domain;

/// <summary>
/// Упорядоченный план восстановления (restore_plans + restore_steps).
/// Три обязательных свойства: порядок шагов значим, план идемпотентен,
/// поддерживает dry-run. Результат каждого шага фиксируется.
/// Персистентность появится на шаге 6 (Restore Engine) — до тех пор
/// записи создаёт только RestoreService.
/// </summary>
public sealed record RestorePlan(
    long Id,
    long SnapshotId,
    DateTimeOffset CreatedAt,
    bool DryRun,
    RestorePlanStatus Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt)
{
    public IReadOnlyList<RestoreStep> Steps { get; init; } = [];
}

/// <summary>Один шаг плана восстановления (restore_steps). Seq — порядок выполнения.</summary>
public sealed record RestoreStep(
    long Id,
    long PlanId,
    int Seq,
    RestoreStrategyKind Strategy,
    string? Target,
    string? ArgsJson,
    RestoreStepResult? Result,
    string? Error,
    long? DurationMs);
