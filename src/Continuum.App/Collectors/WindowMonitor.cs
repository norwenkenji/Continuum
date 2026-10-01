using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Collectors.Native;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;

namespace Continuum.Collectors;

/// <summary>
/// Коллектор процессов и окон. Фокусное окно - GetForegroundWindow;
/// старт/выход процессов - дифф снапшотов CreateToolhelp32Snapshot
/// (Process.GetProcesses() в цикле запрещён бюджетом ресурсов readme).
/// Эмитит только изменения: повторный опрос без изменений - тишина.
/// Собственный процесс сознательно НЕ отфильтровывается - это работа
/// privacy-конвейера (ObservationPipeline), а не коллектора.
/// </summary>
public sealed class WindowMonitor : IObservationSource, IDisposable
{
    /// <summary>Интервал опроса по умолчанию (бюджет readme: опрос окон 2–5 с).</summary>
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);

    private readonly IClock _clock;
    private readonly TimeSpan _pollInterval;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private FocusTracker? _focusTracker;
    private ProcessListDiffer? _processDiffer;

    public WindowMonitor(IClock clock, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (pollInterval is { } interval && interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), interval, "Интервал опроса должен быть положительным.");
        }

        _clock = clock;
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    /// <inheritdoc/>
    public event EventHandler<Observation>? Observed;

    /// <summary>Запускает цикл опроса. Идемпотентно: повторный Start - пустая операция.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_cts is not null)
            {
                return;
            }

            // Перезапуск - новая точка отсчёта: первый снапшот снова базовый
            _focusTracker = new FocusTracker();
            _processDiffer = new ProcessListDiffer();

            var cts = new CancellationTokenSource();
            _cts = cts;
            _loop = Task.Run(() => RunAsync(cts.Token));
        }
    }

    /// <summary>Останавливает цикл опроса. Идемпотентно.</summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }

        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        try
        {
            loop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Фоновая задача не должна ронять Stop
        }

        cts.Dispose();
    }

    public void Dispose() => Stop();

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    Tick();
                }
                catch
                {
                    // Любая ошибка тика (WinAPI, гонка с умершим процессом) -
                    // пропускаем тик, монитор продолжает работу
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Штатная остановка через Stop
        }
    }

    private void Tick()
    {
        // Снапшот процессов один раз за тик: и для диффа, и для имён окон
        var processes = TryTakeProcessSnapshot();

        if (processes is not null && _processDiffer is not null)
        {
            var diff = _processDiffer.Process(processes);
            foreach (var exited in diff.Exited)
            {
                EmitProcessEvent(exited, EventKind.AppExited);
            }

            foreach (var started in diff.Started)
            {
                EmitProcessEvent(started, EventKind.AppStarted);
            }
        }

        TickFocus(processes);
    }

    private void TickFocus(IReadOnlyDictionary<int, string>? processes)
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return; // фокусного окна нет (экран UAC, блокировка и т.п.)
        }

        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pidRaw);
        if (pidRaw == 0)
        {
            return;
        }

        var pid = unchecked((int)pidRaw);
        var title = TryGetWindowTitle(hwnd);

        // Не изменилось (или заголовок не читается) - тишина
        if (_focusTracker is null || !_focusTracker.ShouldEmit(pid, title))
        {
            return;
        }

        string? exeFileName = null;
        processes?.TryGetValue(pid, out exeFileName);

        Emit(new Observation(
            new Observable(
                Kind: ObservableKind.WindowTitle,
                Value: title,
                ProcessId: pid,
                ApplicationName: NormalizeProcessName(exeFileName),
                ApplicationExePath: TryGetProcessImagePath(pid),
                Timestamp: _clock.UtcNow,
                ProjectRootPath: null),
            EventKind.AppFocused));
    }

    private void EmitProcessEvent(ProcessChange change, EventKind kind)
    {
        Emit(new Observation(
            new Observable(
                Kind: ObservableKind.WindowTitle,
                Value: null, // командная строка произвольных процессов - не этот шаг
                ProcessId: change.ProcessId,
                ApplicationName: NormalizeProcessName(change.Name),
                // Для вышедшего процесса путь уже не прочитать - null
                ApplicationExePath: kind == EventKind.AppStarted ? TryGetProcessImagePath(change.ProcessId) : null,
                Timestamp: _clock.UtcNow,
                ProjectRootPath: null),
            kind));
    }

    /// <summary>Заголовок окна; null - не прочитался (пустой, окно умерло между вызовами).</summary>
    private static string? TryGetWindowTitle(IntPtr hwnd)
    {
        var length = NativeMethods.GetWindowTextLengthW(hwnd);
        if (length <= 0)
        {
            return null;
        }

        var capacity = Math.Min(length + 1, NativeMethods.TitleBufferCapacity);
        var buffer = new StringBuilder(capacity);
        var copied = NativeMethods.GetWindowTextW(hwnd, buffer, capacity);
        return copied <= 0 ? null : buffer.ToString(0, copied);
    }

    /// <summary>Снапшот «pid → имя exe» через Toolhelp32; null - снапшот не удался, тик продолжится без диффа.</summary>
    private static Dictionary<int, string>? TryTakeProcessSnapshot()
    {
        var handle = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.SnapshotFlags.Process, 0);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return null;
        }

        try
        {
            var result = new Dictionary<int, string>();
            var entry = new NativeMethods.PROCESSENTRY32W
            {
                dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32W>(),
            };

            if (!NativeMethods.Process32FirstW(handle, ref entry))
            {
                return result;
            }

            do
            {
                if (entry.th32ProcessID != 0 && entry.szExeFile is not null)
                {
                    result[unchecked((int)entry.th32ProcessID)] = entry.szExeFile;
                }
            }
            while (NativeMethods.Process32NextW(handle, ref entry));

            return result;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>Полный путь exe процесса (best-effort); null - нет доступа или процесс уже умер.</summary>
    private static string? TryGetProcessImagePath(int pid)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessAccess.QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(NativeMethods.ExePathBufferCapacity);
            var size = (uint)buffer.Capacity;
            return NativeMethods.QueryFullProcessImageNameW(handle, 0, buffer, ref size)
                ? buffer.ToString()
                : null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>«chrome.exe» → «chrome»; null и пустые строки проходят как null.</summary>
    private static string? NormalizeProcessName(string? exeFileName) =>
        string.IsNullOrWhiteSpace(exeFileName) ? null : Path.GetFileNameWithoutExtension(exeFileName);

    /// <summary>Эмитит наблюдение: ошибка одного подписчика не ломает остальных и сам коллектор.</summary>
    private void Emit(Observation observation)
    {
        var handlers = Observed;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<Observation> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, observation);
            }
            catch
            {
                // Подписчик не должен получать исключения из недр коллектора -
                // и коллектор не должен падать от исключений подписчика
            }
        }
    }
}
