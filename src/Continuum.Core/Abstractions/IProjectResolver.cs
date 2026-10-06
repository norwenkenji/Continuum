namespace Continuum.Core.Abstractions;

/// <summary>
/// Определение проекта по пути (data-sources §4.4): подъём от пути файла
/// или CWD до внешнего git root. Чистая файловая операция, без запуска
/// git.exe. Реализация живёт в оболочке (Infrastructure/Git).
/// </summary>
public interface IProjectResolver
{
    /// <summary>
    /// Нормализованный корень проекта для пути файла или каталога (CWD):
    /// полный путь, регистронезависимый (нормализован к одному регистру),
    /// без завершающего разделителя, с разрешением «..» и subst.
    /// Вложенные репозитории и worktree считаются одним проектом по
    /// внешнему git root; bare-репозитории проектом не считаются.
    /// null - путь вне git-репозитория.
    /// </summary>
    string? ResolveRoot(string path);
}
