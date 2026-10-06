using System;
using System.Collections.Generic;
using Continuum.Core.Domain;

namespace Continuum.Collectors;

/// <summary>Изменение одного dirty-файла между тиками опроса: путь и новый вид изменения.</summary>
public sealed record DirtyFileChange(string Path, FileChangeKind Kind);

/// <summary>
/// Чистая логика диффа dirty-набора git без git: вход - два снапшота
/// (path → kind), выход - появившиеся и сменившие вид записи. Исчезнувшие
/// из dirty (закоммичены, откачены) - не события, пропускаются молча.
/// Первый снапшот после создания или <see cref="Reset"/> - базовая точка
/// отсчёта без событий (как у ProcessListDiffer).
/// </summary>
public sealed class GitStateDiffer
{
    private IReadOnlyDictionary<string, FileChangeKind>? _baseline;

    /// <summary>Сравнивает очередной снапшот с предыдущим и запоминает его как новый базовый.</summary>
    public IReadOnlyList<DirtyFileChange> Process(IReadOnlyDictionary<string, FileChangeKind> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (_baseline is null)
        {
            _baseline = new Dictionary<string, FileChangeKind>(snapshot, StringComparer.Ordinal);
            return [];
        }

        var changes = new List<DirtyFileChange>();
        foreach (var (path, kind) in snapshot)
        {
            if (!_baseline.TryGetValue(path, out var oldKind) || oldKind != kind)
            {
                changes.Add(new DirtyFileChange(path, kind));
            }
        }

        _baseline = new Dictionary<string, FileChangeKind>(snapshot, StringComparer.Ordinal);
        return changes;
    }

    /// <summary>Сбрасывает базовый снапшот: следующий вызов снова отдаст пустой список.</summary>
    public void Reset() => _baseline = null;
}
