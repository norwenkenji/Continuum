using Continuum.Core.Domain;

namespace Continuum.Core.Abstractions;

/// <summary>
/// Наблюдение от коллектора: сырые данные (подлежат privacy-конвейеру)
/// плюс семантика для хронологии. Вид события определяет коллектор -
/// он знает, что именно произошло (смена фокуса, старт процесса и т.д.).
/// </summary>
public sealed record Observation(Observable Observable, EventKind EventKind);
