using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Continuum.Collectors;

/// <summary>Запись снимка процессов для шва тестирования: pid, родитель, имя exe.</summary>
public sealed record ProcessEntry(int Pid, int ParentPid, string Name);

/// <summary>
/// Коллектор терминалов (data-sources §6): снимок процессов через
/// CreateToolhelp32Snapshot, дерево WindowsTerminal с дочерними шеллами
/// (OpenConsole - псевдоконсоль, в строки не попадает, но его дети ищутся),
/// автономные шеллы - отдельными строками. CWD - точное чтение PEB живого
/// процесса (NtQueryInformationProcess + ReadProcessMemory): обновляется
/// при смене каталога в шелле. Класс B/C: отказ доступа к процессу -
/// честная деградация (source = unknown), исключения наружу не выходят.
/// Опрашивается Snapshotter'ом на момент снапшота, не в цикле.
/// </summary>
public class TerminalCollector : ITerminalsProvider
{
    /// <summary>Имя процесса-контейнера вкладок Windows Terminal.</summary>
    private const string TerminalHostName = "WindowsTerminal";

    /// <summary>OpenConsole.exe - псевдоконсоль, не шелл: его CWD бесполезен.</summary>
    private const string PseudoConsoleName = "openconsole";

    /// <summary>Шеллы, дающие строку terminals (§6.1).</summary>
    private static readonly string[] ShellProcessNames = ["cmd", "powershell", "pwsh", "bash"];

    private readonly ILogger<TerminalCollector> _logger;

    public TerminalCollector(ILogger<TerminalCollector> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Терминалы на текущий момент. Строка выдалась - терминал наблюдался
    /// (available = true); CWD мог не прочитаться - тогда source = unknown.
    /// Пустой список - терминалов нет или снимок не удался: не ошибка.
    /// Никогда не бросает.
    /// </summary>
    public async Task<IReadOnlyList<TerminalState>> GetTerminalsAsync(CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(Collect, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Сбор терминалов не удался - снапшот без terminals");
            return [];
        }
    }

    /// <summary>Снимок процессов системы (pid, родитель, имя exe). Шов для тестов.</summary>
    protected virtual IReadOnlyList<ProcessEntry> SnapshotProcesses()
    {
        var result = new List<ProcessEntry>();
        var snapshot = TerminalNativeMethods.CreateToolhelp32Snapshot(
            TerminalNativeMethods.Th32csSnapProcess, 0);
        if (snapshot == new IntPtr(-1))
        {
            return result; // полный сбой снимка - пустой список, не ошибка
        }

        try
        {
            var entry = new TerminalNativeMethods.PROCESSENTRY32W();
            entry.dwSize = (uint)Marshal.SizeOf<TerminalNativeMethods.PROCESSENTRY32W>();
            if (!TerminalNativeMethods.Process32FirstW(snapshot, ref entry))
            {
                return result;
            }

            do
            {
                result.Add(new ProcessEntry(
                    (int)entry.th32ProcessID,
                    (int)entry.th32ParentProcessID,
                    entry.szExeFile));
            }
            while (TerminalNativeMethods.Process32NextW(snapshot, ref entry));
        }
        finally
        {
            TerminalNativeMethods.CloseHandle(snapshot);
        }

        return result;
    }

    /// <summary>
    /// Точный CWD процесса из PEB (§6.2): PEB-&gt;ProcessParameters (x64, смещение 0x20),
    /// CurrentDirectory.DosPath - UNICODE_STRING в ProcessParameters по смещению 0x38.
    /// Смещение подтверждено живым чтением PEB запущенного cmd.exe (дамп
    /// RTL_USER_PROCESS_PARAMETERS x64: 0x10 - ConsoleHandle, 0x20/0x28/0x30 -
    /// StandardInput/Output/Error, 0x38 - CurrentDirectory.DosPath).
    /// false - нет доступа (OpenProcess вернул Zero: AppContainer, повышение) или
    /// чтение не удалось. Шов для тестов.
    /// </summary>
    protected virtual bool TryReadCurrentDirectory(int pid, out string? cwd)
    {
        cwd = null;
        var process = TerminalNativeMethods.OpenProcess(
            TerminalNativeMethods.ProcessQueryVmRead, false, pid);
        if (process == IntPtr.Zero)
        {
            return false; // ACCESS_DENIED (Store-терминал в AppContainer) - fallback источника
        }

        try
        {
            // PROCESS_BASIC_INFORMATION (x64): PebBaseAddress по смещению 0x08
            var basicInfo = new byte[48];
            var status = TerminalNativeMethods.NtQueryInformationProcess(
                process,
                TerminalNativeMethods.ProcessBasicInformation,
                basicInfo,
                basicInfo.Length,
                IntPtr.Zero);
            if (status != 0)
            {
                return false;
            }

            var peb = BitConverter.ToInt64(basicInfo, 0x08);
            if (peb == 0)
            {
                return false;
            }

            // PEB-&gt;ProcessParameters: указатель по смещению 0x20 (x64)
            if (!TryReadBytes(process, peb + 0x20, 8, out var parameterBytes))
            {
                return false;
            }

            var parameters = BitConverter.ToInt64(parameterBytes, 0);
            if (parameters == 0)
            {
                return false;
            }

            // CurrentDirectory.DosPath: UNICODE_STRING с смещения 0x38 в
            // RTL_USER_PROCESS_PARAMETERS (x64): Length(0x00), MaxLength(0x02),
            // паддинг 4, Buffer(0x08)
            if (!TryReadBytes(process, parameters + 0x38, 16, out var unicodeString))
            {
                return false;
            }

            var length = BitConverter.ToUInt16(unicodeString, 0);
            var buffer = BitConverter.ToInt64(unicodeString, 8);
            if (length == 0 || buffer == 0)
            {
                return false;
            }

            if (!TryReadBytes(process, buffer, length, out var pathBytes))
            {
                return false;
            }

            cwd = Encoding.Unicode.GetString(pathBytes).TrimEnd('\\', '/');
            return true;
        }
        finally
        {
            TerminalNativeMethods.CloseHandle(process);
        }
    }

    /// <summary>Чтение байтов памяти процесса: без марширования структур, через BitConverter.</summary>
    private static bool TryReadBytes(IntPtr process, long address, int size, out byte[] buffer)
    {
        buffer = new byte[size];
        return TerminalNativeMethods.ReadProcessMemory(
            process,
            new IntPtr(address),
            buffer,
            new IntPtr(size),
            out var bytesRead)
            && bytesRead.ToInt64() == size;
    }

    private IReadOnlyList<TerminalState> Collect()
    {
        var processes = SnapshotProcesses();
        if (processes.Count == 0)
        {
            return [];
        }

        // Свой pid и системные (pid <= 4) не наблюдаются
        var ownPid = Environment.ProcessId;
        var candidates = processes
            .Where(p => p.Pid > 4 && p.Pid != ownPid)
            .ToList();
        var childrenByParent = candidates.ToLookup(p => p.ParentPid);

        var result = new List<TerminalState>();
        var claimedPids = new HashSet<int>();

        // WindowsTerminal - контейнер вкладок: его шеллы (прямые и транзитивные
        // дети, OpenConsole пропускается как узел) дают строки Host=WindowsTerminal
        foreach (var host in candidates.Where(p => IsName(p.Name, "windowsterminal")))
        {
            var shells = new List<ProcessEntry>();
            CollectDescendantShells(host.Pid, childrenByParent, shells, new HashSet<int>());
            foreach (var shell in shells)
            {
                claimedPids.Add(shell.Pid);
                result.Add(BuildState(TerminalHostName, shell));
            }

            if (shells.Count == 0)
            {
                // Терминал запущен, но ни одного читаемого ребёнка - одна строка Unknown
                result.Add(new TerminalState(
                    Id: 0, SnapshotId: 0, Available: true,
                    Host: TerminalHostName, Shell: null, Cwd: null,
                    Source: TerminalSource.Unknown));
            }
        }

        // Автономные шеллы вне деревьев WindowsTerminal
        foreach (var process in candidates)
        {
            if (claimedPids.Contains(process.Pid)
                || IsName(process.Name, PseudoConsoleName)
                || !IsShell(process.Name))
            {
                continue;
            }

            var host = NameWithoutExtension(process.Name);
            result.Add(BuildState(host, process));
        }

        return result;
    }

    /// <summary>Обход поддерева процессов: шеллы собираются, OpenConsole пропускается, но его дети ищутся.</summary>
    private static void CollectDescendantShells(
        int pid,
        ILookup<int, ProcessEntry> childrenByParent,
        List<ProcessEntry> shells,
        HashSet<int> visited)
    {
        if (!visited.Add(pid))
        {
            return; // защита от циклов в родительских ссылках
        }

        foreach (var child in childrenByParent[pid])
        {
            if (IsShell(child.Name))
            {
                shells.Add(child);
            }

            // Транзитивно: cmd, запущенный из pwsh внутри того же терминала, тоже вкладка
            CollectDescendantShells(child.Pid, childrenByParent, shells, visited);
        }
    }

    /// <summary>Строка терминала: CWD читается на момент снапшота; сбой - честный Unknown.</summary>
    private TerminalState BuildState(string host, ProcessEntry shell)
    {
        string? cwd = null;
        var source = TerminalSource.Unknown;
        try
        {
            if (TryReadCurrentDirectory(shell.Pid, out var directory)
                && !string.IsNullOrEmpty(directory))
            {
                cwd = directory;
                source = TerminalSource.Peb;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Чтение CWD процесса {Pid} ({Name}) не удалось", shell.Pid, shell.Name);
        }

        return new TerminalState(
            Id: 0,
            SnapshotId: 0,
            Available: true,
            Host: host,
            Shell: NameWithoutExtension(shell.Name),
            Cwd: cwd,
            Source: source);
    }

    private static bool IsShell(string processName) =>
        ShellProcessNames.Any(name => IsName(processName, name));

    private static bool IsName(string processName, string expected) =>
        string.Equals(NameWithoutExtension(processName), expected, StringComparison.OrdinalIgnoreCase);

    private static string NameWithoutExtension(string processName) =>
        processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
}
