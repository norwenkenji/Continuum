using Continuum.Core.Domain;

namespace Continuum.Core.Abstractions;

/// <summary>
/// Наблюдение от коллектора: сырые данные (подлежат privacy-конвейеру)
/// плюс семантика для хронологии. Вид события определяет коллектор -
/// он знает, что именно произошло (смена фокуса, старт процесса и т.д.).
///
/// FileChange/FileSource обязательны для событий FileChanged/FileOpened
/// (иначе пайплайн не сможет записать file_activity и отбросит наблюдение
/// с предупреждением в лог); для остальных событий не заполняются.
/// </summary>
public sealed record Observation(
    Observable Observable,
    EventKind EventKind,
    FileChangeKind? FileChange = null,
    FileActivitySource? FileSource = null);
