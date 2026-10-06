namespace Continuum.Core.Abstractions;

/// <summary>
/// Реестр проектов текущей сессии: какие git root'ы уже обнаружены и под
/// каким id лежат в БД. Регистрируют коллекторы/швы, читают мониторы
/// (ProjectGitMonitor) и пайплайн. Реализация живёт в оболочке (Projects/).
/// </summary>
public interface IProjectRegistry
{
    /// <summary>Нормализованные корни зарегистрированных проектов.</summary>
    IReadOnlyCollection<string> RegisteredRoots { get; }

    /// <summary>
    /// Регистрирует путь (файла или каталога): определяет проект,
    /// при первом появлении делает upsert в БД. Возвращает id проекта
    /// или null, если путь вне git-репозитория. Потокобезопасно.
    /// </summary>
    Task<long?> RegisterPathAsync(string path, CancellationToken ct = default);

    /// <summary>Id проекта по нормализованному корню; null - не зарегистрирован.</summary>
    long? GetProjectId(string normalizedRoot);
}
