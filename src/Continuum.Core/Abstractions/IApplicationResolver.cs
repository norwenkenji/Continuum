namespace Continuum.Core.Abstractions;

/// <summary>
/// Резолвер путей приложений (readme «Порядок доведения», шаг 6):
/// App Paths → Uninstall → известные расположения → PATH. Нужен, чтобы
/// RestorePlan не падал на машине, где code/wt/chrome не в PATH.
/// Реализация живёт в оболочке (Restore/ApplicationPathResolver).
/// </summary>
public interface IApplicationResolver
{
    /// <summary>
    /// Путь к исполняемому файлу приложения по имени («code», «wt», «chrome»
    /// или имя процесса). Null - не найден нигде: шаг плана будет failed
    /// с причиной, остальные шаги продолжат выполняться.
    /// Результат кэшируется на время жизни процесса.
    /// </summary>
    string? ResolveExecutable(string applicationName);
}
