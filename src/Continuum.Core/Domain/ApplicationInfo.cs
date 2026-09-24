namespace Continuum.Core.Domain;

/// <summary>
/// Наблюдаемое приложение. Названо ApplicationInfo, а не Application,
/// чтобы не конфликтовать с System.Windows.Application в WPF-оболочке.
/// ExePath может быть неизвестен — тогда в БД пишется пустая строка
/// (уникальность по (name, exe_path)).
/// </summary>
public sealed record ApplicationInfo(
    long Id,
    string Name,
    string? ExePath,
    string? AdapterKey,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);
