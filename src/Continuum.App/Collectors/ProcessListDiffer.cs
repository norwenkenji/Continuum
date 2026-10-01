using System;
using System.Collections.Generic;

namespace Continuum.Collectors;

/// <summary>Одно изменение в списке процессов: PID и имя exe.</summary>
public sealed record ProcessChange(int ProcessId, string Name);

/// <summary>Результат сравнения двух снапшотов процессов.</summary>
public sealed record ProcessDiff(IReadOnlyList<ProcessChange> Started, IReadOnlyList<ProcessChange> Exited)
{
    /// <summary>Пустой дифф: ни стартов, ни выходов.</summary>
    public static readonly ProcessDiff Empty = new([], []);
}

/// <summary>
/// Чистая логика диффа снапшотов процессов (pid → имя exe) без WinAPI.
/// Первый снапшот после создания или <see cref="Reset"/> - базовая точка
/// отсчёта, события не эмитятся: иначе запуск монитора «заспамил» бы
/// app_started всеми уже работающими процессами системы.
/// </summary>
public sealed class ProcessListDiffer
{
    private IReadOnlyDictionary<int, string>? _baseline;

    /// <summary>Сравнивает очередной снапшот с предыдущим и запоминает его как новый базовый.</summary>
    public ProcessDiff Process(IReadOnlyDictionary<int, string> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (_baseline is null)
        {
            _baseline = new Dictionary<int, string>(snapshot);
            return ProcessDiff.Empty;
        }

        var started = new List<ProcessChange>();
        var exited = new List<ProcessChange>();

        foreach (var (pid, name) in snapshot)
        {
            if (!_baseline.TryGetValue(pid, out var oldName))
            {
                started.Add(new ProcessChange(pid, name));
            }
            else if (!string.Equals(oldName, name, StringComparison.OrdinalIgnoreCase))
            {
                // PID переиспользован другим процессом: старый вышел, новый стартовал
                exited.Add(new ProcessChange(pid, oldName));
                started.Add(new ProcessChange(pid, name));
            }
        }

        foreach (var (pid, name) in _baseline)
        {
            if (!snapshot.ContainsKey(pid))
            {
                exited.Add(new ProcessChange(pid, name));
            }
        }

        _baseline = new Dictionary<int, string>(snapshot);
        return new ProcessDiff(started, exited);
    }

    /// <summary>Сбрасывает базовый снапшот: следующий вызов снова отдаст пустой дифф.</summary>
    public void Reset() => _baseline = null;
}
