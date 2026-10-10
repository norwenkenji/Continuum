using Continuum.Core.Domain;

namespace Continuum.Core.Abstractions;

/// <summary>
/// Снапшоттер: снимает точку состояния контекста (snapshots + git_state).
/// Реализация живёт в оболочке (Snapshots/Snapshotter).
/// </summary>
public interface ISnapshotter
{
    /// <summary>
    /// Собирает и сохраняет снапшот указанной причины. Возвращает id снапшота.
    /// Никогда не бросает из-за недоступного git: проекты без git пишутся
    /// с available=0 (критерий readme).
    /// </summary>
    Task<long> SaveSnapshotAsync(SnapshotReason reason, CancellationToken ct = default);
}
