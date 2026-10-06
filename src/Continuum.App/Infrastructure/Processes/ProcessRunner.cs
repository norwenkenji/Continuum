using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;

namespace Continuum.Infrastructure.Processes;

/// <summary>
/// Запуск внешних процессов (реализация IProcessRunner). stdout/stderr
/// читаются асинхронно и параллельно - иначе возможна взаимная блокировка
/// на полном буфере. По таймауту и по внешней отмене всё дерево процессов
/// убивается целиком. Ненулевой код выхода - данные результата, а не
/// исключение. Исключение возможно только на этапе Process.Start
/// (например, exe не найден) - это обязан обработать вызывающий.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Единственный потребитель - git: его вывод - UTF-8 (пути с
            // кириллицей иначе превращаются в мойбак консольной кодировки)
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                // Пара перезаписывает унаследованную переменную с тем же именем
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Оба потока читаются сразу и асинхронно: последовательное синхронное
        // чтение - классическая взаимная блокировка на полном буфере stderr
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Таймаут: убиваем всё дерево, возвращаем то, что успели прочитать
            KillTree(process);
            return new ProcessResult(
                -1,
                await SafeReadAsync(stdoutTask).ConfigureAwait(false),
                await SafeReadAsync(stderrTask).ConfigureAwait(false),
                TimedOut: true);
        }
        catch (OperationCanceledException)
        {
            // Внешняя отмена: тоже убиваем дерево, потом честно отменяемся
            KillTree(process);
            await SafeReadAsync(stdoutTask).ConfigureAwait(false);
            await SafeReadAsync(stderrTask).ConfigureAwait(false);
            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false),
            TimedOut: false);
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Процесс мог завершиться сам между срабатыванием таймаута и Kill
        }
    }

    private static async Task<string> SafeReadAsync(Task<string> readTask)
    {
        try
        {
            return await readTask.ConfigureAwait(false);
        }
        catch
        {
            // Поток мог оборваться вместе с убитым процессом
            return string.Empty;
        }
    }
}
