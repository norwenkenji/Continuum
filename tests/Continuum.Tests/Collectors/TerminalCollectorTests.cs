using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Collectors;
using Continuum.Core.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Continuum.Tests.Collectors;

/// <summary>
/// TerminalCollector: живой тест PEB (запуск cmd.exe и чтение его CWD -
/// подтверждает смещения PEB/ProcessParameters/CurrentDirectory) и
/// шовные тесты дерева WindowsTerminal (швы SnapshotProcesses +
/// TryReadCurrentDirectory подменяются наследником).
/// </summary>
public class TerminalCollectorTests
{
    [Fact]
    public async Task Live_cmd_process_cwd_is_read_from_peb()
    {
        // Живой процесс: смещения PEB обязаны сойтись (data-sources §6.2)
        var tempDir = Path.Combine(
            Path.GetTempPath(), "continuum-terminal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        // %TEMP% бывает в 8.3-короткой форме (NORWEN~1), а PEB хранит длинную
        // (norwenkenji): ожидание разрешается тем же механизмом Windows -
        // через текущий каталог процесса (GetCurrentDirectory возвращает
        // длинную форму, как и PEB cmd после cd /d)
        var expectedCwd = ResolveLongPath(tempDir);
        var collector = new TerminalCollector(NullLogger<TerminalCollector>.Instance);

        var startInfo = new ProcessStartInfo("cmd.exe", $"/k cd /d \"{tempDir}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        try
        {
            var ready = WaitFor(() =>
            {
                var terminals = collector.GetTerminalsAsync().GetAwaiter().GetResult();
                return terminals.Any(t => t.Host == "cmd"
                    && string.Equals(t.Cwd, expectedCwd, StringComparison.OrdinalIgnoreCase));
            }, timeoutMs: 5000);
            Assert.True(ready, "cmd не сменил CWD за 5 секунд - смещения PEB не сошлись");

            var terminals = await collector.GetTerminalsAsync();
            var row = terminals.Single(t => t.Host == "cmd"
                && string.Equals(t.Cwd, expectedCwd, StringComparison.OrdinalIgnoreCase));
            Assert.Equal("cmd", row.Shell);
            Assert.Equal(TerminalSource.Peb, row.Source);
            Assert.True(row.Available);
        }
        finally
        {
            try
            {
                process!.Kill(entireProcessTree: true);
            }
            catch
            {
                // Процесс мог завершиться сам - не страшно
            }

            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Каталог мог быть занят - мусор в %TEMP% не ошибка теста
            }
        }
    }

    /// <summary>
    /// Длинная форма пути без P/Invoke: смена текущего каталога процесса
    /// и чтение его обратно (Windows разрешает 8.3-имена в длинные).
    /// Восстанавливает прежний каталог; чужие тесты абсолютных путей не задевает.
    /// </summary>
    private static string ResolveLongPath(string path)
    {
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(path);
            return Directory.GetCurrentDirectory();
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    [Fact]
    public async Task WindowsTerminal_children_become_rows_and_openconsole_is_skipped()
    {
        // Дерево: WT(100) -> pwsh(101); WT(100) -> OpenConsole(103) - не строка;
        // cmd(102) с родителем System - автономный шелл
        var processes = new[]
        {
            new ProcessEntry(100, 1, "WindowsTerminal.exe"),
            new ProcessEntry(101, 100, "pwsh.exe"),
            new ProcessEntry(102, 1, "cmd.exe"),
            new ProcessEntry(103, 100, "OpenConsole.exe"),
        };
        var collector = new StubTerminalCollector(processes, pid => pid switch
        {
            101 => (true, @"C:\work\project"),
            102 => (true, @"C:\other"),
            _ => (false, null),
        });

        var terminals = await collector.GetTerminalsAsync();

        Assert.Equal(2, terminals.Count);
        var wtRow = terminals.Single(t => t.Host == "WindowsTerminal");
        Assert.Equal("pwsh", wtRow.Shell);
        Assert.Equal(@"C:\work\project", wtRow.Cwd);
        Assert.Equal(TerminalSource.Peb, wtRow.Source);

        // OpenConsole - псевдоконсоль: ни строкой, ни шеллом
        Assert.DoesNotContain(terminals, t =>
            t.Host.Contains("OpenConsole", StringComparison.OrdinalIgnoreCase)
            || (t.Shell ?? string.Empty).Contains("OpenConsole", StringComparison.OrdinalIgnoreCase));

        var standalone = terminals.Single(t => t.Host == "cmd");
        Assert.Equal("cmd", standalone.Shell);
        Assert.Equal(@"C:\other", standalone.Cwd);
        Assert.Equal(TerminalSource.Peb, standalone.Source);
    }

    [Fact]
    public async Task Unreadable_process_gives_row_with_unknown_source()
    {
        var processes = new[]
        {
            new ProcessEntry(100, 1, "WindowsTerminal.exe"),
            new ProcessEntry(101, 100, "pwsh.exe"),
        };
        var collector = new StubTerminalCollector(processes, _ => (false, null));

        var terminals = await collector.GetTerminalsAsync();

        var row = Assert.Single(terminals);
        Assert.Equal("WindowsTerminal", row.Host);
        Assert.Equal("pwsh", row.Shell);
        Assert.Null(row.Cwd); // ACCESS_DENIED - строка есть, CWD нет
        Assert.Equal(TerminalSource.Unknown, row.Source);
        Assert.True(row.Available);
    }

    [Fact]
    public async Task WindowsTerminal_without_readable_children_gives_single_unknown_row()
    {
        // Дети WT нет вовсе (или только OpenConsole) - одна строка Unknown
        var processes = new[]
        {
            new ProcessEntry(100, 1, "WindowsTerminal.exe"),
            new ProcessEntry(103, 100, "OpenConsole.exe"),
        };
        var collector = new StubTerminalCollector(processes, _ => (false, null));

        var terminals = await collector.GetTerminalsAsync();

        var row = Assert.Single(terminals);
        Assert.Equal("WindowsTerminal", row.Host);
        Assert.Null(row.Shell);
        Assert.Null(row.Cwd);
        Assert.Equal(TerminalSource.Unknown, row.Source);
    }

    [Fact]
    public async Task Own_pid_is_skipped_and_empty_snapshot_gives_empty_list()
    {
        var ownPid = Environment.ProcessId;
        var collector = new StubTerminalCollector(
        [
            new ProcessEntry(ownPid, 1, "cmd.exe"),
            new ProcessEntry(200, 1, "cmd.exe"),
        ], _ => (true, @"C:\work"));
        var terminals = await collector.GetTerminalsAsync();
        Assert.Single(terminals); // свой pid не наблюдается, чужой - строка
        Assert.Equal("cmd", terminals[0].Host);

        var empty = await new StubTerminalCollector([]).GetTerminalsAsync();
        Assert.Empty(empty);
    }

    [Fact]
    public async Task Throwing_reader_gives_unknown_row_without_exception()
    {
        var collector = new StubTerminalCollector(
            [new ProcessEntry(200, 1, "cmd.exe")],
            _ => throw new InvalidOperationException("сбой чтения памяти"));

        var terminals = await collector.GetTerminalsAsync(); // не бросает

        var row = Assert.Single(terminals);
        Assert.Equal("cmd", row.Host);
        Assert.Equal("cmd", row.Shell);
        Assert.Null(row.Cwd);
        Assert.Equal(TerminalSource.Unknown, row.Source);
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return condition();
    }

    /// <summary>Подмена обоих швов: снимок процессов и чтение CWD детерминированы.</summary>
    private sealed class StubTerminalCollector : TerminalCollector
    {
        private readonly IReadOnlyList<ProcessEntry> _processes;
        private readonly Func<int, (bool Ok, string? Cwd)> _readDirectory;

        public StubTerminalCollector(
            IReadOnlyList<ProcessEntry> processes,
            Func<int, (bool Ok, string? Cwd)>? readDirectory = null)
            : base(NullLogger<TerminalCollector>.Instance)
        {
            _processes = processes;
            _readDirectory = readDirectory ?? (_ => (false, null));
        }

        protected override IReadOnlyList<ProcessEntry> SnapshotProcesses() => _processes;

        protected override bool TryReadCurrentDirectory(int pid, out string? cwd)
        {
            var (ok, directory) = _readDirectory(pid);
            cwd = directory;
            return ok;
        }
    }
}
