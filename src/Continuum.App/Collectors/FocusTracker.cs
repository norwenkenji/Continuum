using System;

namespace Continuum.Collectors;

/// <summary>
/// Чистая логика «смена фокуса» без WinAPI: хранит предыдущую пару
/// (pid, title) и решает, эмитить ли событие. Критерий шага 2:
/// ровно одно событие на изменение пары; повторный опрос без изменений -
/// тишина.
/// </summary>
public sealed class FocusTracker
{
    private int _processId;
    private string? _title;
    private bool _hasState;

    /// <summary>
    /// Фиксирует текущее наблюдение фокуса. Возвращает true ровно один раз
    /// на смену пары (pid, title). Пустой заголовок и неположительный pid -
    /// «не смогли прочитать»: события нет и состояние не портится, поэтому
    /// возврат к прежнему окну после мусорного чтения не эмитится повторно.
    /// </summary>
    public bool ShouldEmit(int processId, string? title)
    {
        if (processId <= 0 || string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        if (_hasState && _processId == processId && string.Equals(_title, title, StringComparison.Ordinal))
        {
            return false;
        }

        _processId = processId;
        _title = title;
        _hasState = true;
        return true;
    }

    /// <summary>Сбрасывает состояние: перезапуск монитора - новая точка отсчёта.</summary>
    public void Reset()
    {
        _processId = 0;
        _title = null;
        _hasState = false;
    }
}
