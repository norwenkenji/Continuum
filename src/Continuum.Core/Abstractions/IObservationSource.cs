namespace Continuum.Core.Abstractions;

/// <summary>
/// Источник наблюдений (коллектор). Эмитит наблюдения через <see cref="Observed"/>;
/// сам ничего не фильтрует, не маскирует и не пишет в БД - судьбу наблюдения
/// решает privacy-конвейер (ObservationPipeline).
///
/// Требования к реализации:
/// - Start/Stop идемпотентны;
/// - обработчики события не должны получать исключения из недр коллектора;
/// - «ничего не изменилось» не эмитится (события - на изменение, не на тик).
/// </summary>
public interface IObservationSource
{
    event EventHandler<Observation>? Observed;

    void Start();

    void Stop();
}
