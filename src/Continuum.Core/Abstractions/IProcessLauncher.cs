namespace Continuum.Core.Abstractions;

/// <summary>
/// Запуск процессов для восстановления (RestoreEngine). Отдельно от
/// IProcessRunner: тому нужно ждать завершения и читать потоки (git),
/// этому - запустить и не ждать (приложения пользователя). Шов для тестов:
/// исполнитель плана проверяется без реального запуска чего-либо.
/// </summary>
public interface IProcessLauncher
{
    /// <summary>
    /// Запускает процесс (UseShellExecute - работают ассоциации файлов и URL).
    /// Возвращает false при любой неудаче, не бросает.
    /// </summary>
    bool TryStart(string fileName, string? arguments = null, string? workingDirectory = null);
}
