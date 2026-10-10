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
using Microsoft.Extensions.Logging;

namespace Continuum.Collectors;

/// <summary>
/// Коллектор недавно открытых документов (data-sources §3.2): опрос
/// %APPDATA%\Microsoft\Windows\Recent\*.lnk, цель ярлыка разрешается через
/// IShellLink (COM). Эмитит FileOpened/Opened/Recent - это открытия, а не
/// изменения. Первый опрос - базовый снимок без эмиссий, иначе старт
/// приложения заспамил бы событиями всю историю.
///
/// Дедупликация: (целевой путь, File.GetLastWriteTime(lnk)); новая пара -
/// новое открытие. На имя .lnk не полагаемся (в некоторых версиях Windows
/// оно содержит timestamp - источник ненадёжный). Проект для открытых
/// документов здесь не определяется (ProjectRootPath = null).
/// </summary>
public sealed class RecentFilesMonitor : IObservationSource, IDisposable
{
    /// <summary>Интервал опроса по умолчанию.</summary>
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(30);

    private readonly IClock _clock;
    private readonly ILogger<RecentFilesMonitor> _logger;
    private readonly string _recentFolder;
    private readonly Func<string, string?> _resolveTarget;
    private readonly TimeSpan _pollInterval;
    private readonly object _gate = new();

    // Целевой путь → mtime ярлыка при последней эмиссии (регистронезависимо: Windows)
    private readonly Dictionary<string, DateTime> _lastEmitted = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _baselineTaken;

    /// <param name="recentFolder">Папка Recent; null - стандартная %APPDATA%\Microsoft\Windows\Recent.</param>
    /// <param name="resolveTarget">Шов разрешения цели ярлыка; null - COM IShellLink. Для тестов.</param>
    public RecentFilesMonitor(
        IClock clock,
        ILogger<RecentFilesMonitor> logger,
        string? recentFolder = null,
        Func<string, string?>? resolveTarget = null,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        if (pollInterval is { } interval && interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), interval, "Интервал опроса должен быть положительным.");
        }

        _clock = clock;
        _logger = logger;
        _recentFolder = recentFolder
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Recent");
        _resolveTarget = resolveTarget ?? ResolveTargetViaCom;
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

            // Перезапуск - новая точка отсчёта: первый опрос снова базовый
            _lastEmitted.Clear();
            _baselineTaken = false;

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
                    // Любая ошибка тика - пропускаем тик, коллектор продолжает работу
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
        IEnumerable<string> links;
        try
        {
            if (!Directory.Exists(_recentFolder))
            {
                return; // папки Recent нет - источник недоступен, тихая деградация
            }

            links = Directory.EnumerateFiles(_recentFolder, "*.lnk");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось перечислить ярлыки в {Folder}", _recentFolder);
            return;
        }

        var isBaseline = !_baselineTaken;
        _baselineTaken = true;

        foreach (var link in links)
        {
            try
            {
                var target = _resolveTarget(link);
                if (string.IsNullOrEmpty(target))
                {
                    continue; // битый ярлык - пропуск ярлыка, не опроса
                }

                // File.Exists ложен и для каталогов: покрывает фильтр «это файл»
                if (!File.Exists(target) || IsInsideRecentFolder(target))
                {
                    continue;
                }

                var linkMtime = File.GetLastWriteTime(link);
                if (isBaseline)
                {
                    // Базовый снимок: запоминаем текущее состояние без эмиссий
                    _lastEmitted[target] = linkMtime;
                    continue;
                }

                if (_lastEmitted.TryGetValue(target, out var lastMtime) && lastMtime == linkMtime)
                {
                    continue; // уже эмитили это открытие
                }

                _lastEmitted[target] = linkMtime;

                Emit(new Observation(
                    new Observable(
                        Kind: ObservableKind.FilePath,
                        Value: target,
                        ProcessId: null,
                        ApplicationName: null,
                        ApplicationExePath: null,
                        Timestamp: _clock.UtcNow,
                        ProjectRootPath: null),
                    EventKind.FileOpened,
                    FileChange: FileChangeKind.Opened,
                    FileSource: FileActivitySource.Recent));
            }
            catch
            {
                // Сбой одного ярлыка - пропуск ярлыка, не опроса
            }
        }
    }

    /// <summary>Цель лежит внутри самой папки Recent (ярлык на ярлык) - такие пропускаем.</summary>
    private bool IsInsideRecentFolder(string target)
    {
        var folderPrefix = _recentFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return target.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Разрешение цели ярлыка через IShellLink (COM). Любой сбой - null.</summary>
    private static string? ResolveTargetViaCom(string linkPath)
    {
        object? shellLink = null;
        try
        {
            shellLink = new ShellLinkNativeMethods.ShellLinkObject();
            var persistFile = (ShellLinkNativeMethods.IPersistFile)shellLink;
            if (persistFile.Load(linkPath, ShellLinkNativeMethods.StgmRead) != 0)
            {
                return null;
            }

            var buffer = new StringBuilder(ShellLinkNativeMethods.TargetBufferCapacity);
            ((ShellLinkNativeMethods.IShellLinkW)shellLink).GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
            return buffer.Length == 0 ? null : buffer.ToString();
        }
        catch
        {
            return null; // COM недоступен или ярлык битый - пропуск
        }
        finally
        {
            if (shellLink is not null && Marshal.IsComObject(shellLink))
            {
                Marshal.ReleaseComObject(shellLink);
            }
        }
    }

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
