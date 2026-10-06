namespace Continuum.Core.Abstractions;

/// <summary>Результат запуска внешнего процесса.</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

/// <summary>
/// Запуск внешних процессов. Шов для тестов: компоненты вроде GitClient
/// проверяются на фейковом runner'е без реальных процессов.
/// Реализация живёт в оболочке (Infrastructure/Processes).
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Запускает exe с аргументами в рабочем каталоге и ждёт завершения
    /// до timeout. При таймауте обязан убить всё дерево процессов.
    /// Ненулевой код выхода - не исключение, а данные в результате.
    /// </summary>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        TimeSpan timeout,
        CancellationToken ct = default);
}
