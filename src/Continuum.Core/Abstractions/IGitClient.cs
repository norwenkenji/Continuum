using Continuum.Core.Domain;

namespace Continuum.Core.Abstractions;

/// <summary>Один dirty-файл из «git --no-optional-locks status --porcelain».</summary>
public sealed record DirtyFile(string Path, FileChangeKind Kind);

/// <summary>
/// Состояние git-репозитория на момент опроса. Зеркалит таблицу git_state
/// (available/branch/head_*) плюс список dirty-файлов для file_activity.
/// Available=false: git не найден или путь не репозиторий - остальные
/// поля null/пусты, исключений нет.
/// </summary>
public sealed record GitStateInfo(
    bool Available,
    string? Branch,
    string? HeadCommit,
    string? HeadSubject,
    DateTimeOffset? HeadTimestamp,
    IReadOnlyList<DirtyFile> DirtyFiles)
{
    /// <summary>Единственный способ сказать «git недоступен».</summary>
    public static readonly GitStateInfo Unavailable = new(false, null, null, null, null, []);
}

/// <summary>
/// Единственный компонент системы, запускающий git.exe (data-sources §4).
/// Только читающие команды §4.2, окружение §4.3, таймаут с убийством дерева.
/// Реализация живёт в оболочке (Infrastructure/Git).
/// </summary>
public interface IGitClient
{
    /// <summary>Путь к git.exe (кэшируется на время жизни процесса); null - git не найден нигде.</summary>
    string? FindGitExe();

    /// <summary>
    /// Корень репозитория для пути (git rev-parse --show-toplevel);
    /// null - путь вне репозитория или git недоступен.
    /// </summary>
    Task<string?> GetRepositoryRootAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Полное состояние репозитория: ветка, HEAD, dirty-файлы.
    /// Никогда не бросает: любая проблема - GitStateInfo.Unavailable.
    /// </summary>
    Task<GitStateInfo> GetStateAsync(string repositoryRoot, CancellationToken ct = default);
}
