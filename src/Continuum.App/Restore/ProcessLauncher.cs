using System;
using System.Diagnostics;
using Continuum.Core.Abstractions;

namespace Continuum.Restore;

/// <summary>
/// Запуск процессов для восстановления: UseShellExecute = true - работают
/// ассоциации файлов и URL. Не ждёт завершения и не читает потоки (в отличие
/// от IProcessRunner для git). Любая неудача - false, не исключение.
/// </summary>
public sealed class ProcessLauncher : IProcessLauncher
{
    /// <inheritdoc/>
    public bool TryStart(string fileName, string? arguments = null, string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = true,
            };
            if (!string.IsNullOrEmpty(arguments))
            {
                startInfo.Arguments = arguments;
            }

            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                startInfo.WorkingDirectory = workingDirectory;
            }

            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch
        {
            // путь не существует, ассоциация сломана, доступ запрещён - не исключение
            return false;
        }
    }
}
