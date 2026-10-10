using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Continuum.Collectors;

/// <summary>
/// Коллектор файловой активности по проектам (data-sources §3.1):
/// FileSystemWatcher на каждом зарегистрированном git root'е. События -
/// только на изменения (Created/Changed/Deleted/Renamed), с исключением
/// служебных каталогов и дедупликацией по (path, change_kind).
///
/// Осознанная семантика Renamed (упрощение, зафиксировано заданием):
/// переименование эмитится как Modified по НОВОМУ пути (FullPath), без
/// различения «раньше не видели - Added». Редакторы, пишущие через
/// temp+rename, могут дать пару Added(temp-файл) + Modified(целевой файл) -
/// допустимый шум; повторные сохранения того же файла в пределах окна
/// дедупликации схлопываются в одно событие.
/// </summary>
public sealed class FileActivityWatcher : IObservationSource, IDisposable
{
    /// <summary>Период сверки набора наблюдателей с реестром проектов (норма: 5-10 с).</summary>
    private static readonly TimeSpan DefaultSyncInterval = TimeSpan.FromSeconds(7);

    /// <summary>Окно дедупликации одного (path, change_kind).</summary>
    private static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(2);

    /// <summary>Возраст записи дедуп-кэша, после которого она вычищается.</summary>
    private static readonly TimeSpan DedupEntryTtl = TimeSpan.FromMinutes(1);

    /// <summary>Буфер FileSystemWatcher (§3.1: 64 КБ).</summary>
    private const int InternalBufferSizeBytes = 64 * 1024;

    /// <summary>Исключённые каталоги (§3.1): путь с таким сегментом не эмитится.</summary>
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", ".vs", "AppData", "packages",
        "target", "dist", "build", "__pycache__", ".venv", "vendor",
    };

    private readonly IProjectRegistry _registry;
    private readonly IClock _clock;
    private readonly ILogger<FileActivityWatcher> _logger;
    private readonly TimeSpan _syncInterval;
    private readonly object _gate = new();

    // Корень проекта (нормализован реестром к одному регистру) → его наблюдатель
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.Ordinal);

    // Корни, чей наблюдатель упал (Error) - пересоздаются на ближайшем тике сверки
    private readonly HashSet<string> _failedRoots = new(StringComparer.Ordinal);

    // Дедуп-кэш: (путь в нижнем регистре, вид изменения) → время последней эмиссии
    private readonly ConcurrentDictionary<(string Path, FileChangeKind Kind), DateTimeOffset> _recentEmissions = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _running;

    public FileActivityWatcher(
        IProjectRegistry registry,
        IClock clock,
        ILogger<FileActivityWatcher> logger,
        TimeSpan? syncInterval = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        if (syncInterval is { } interval && interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(syncInterval), interval, "Интервал сверки должен быть положительным.");
        }

        _registry = registry;
        _clock = clock;
        _logger = logger;
        _syncInterval = syncInterval ?? DefaultSyncInterval;
    }

    /// <inheritdoc/>
    public event EventHandler<Observation>? Observed;

    /// <summary>Запускает наблюдение. Идемпотентно: повторный Start - пустая операция.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_cts is not null)
            {
                return;
            }

            // Перезапуск - новая точка отсчёта
            _recentEmissions.Clear();
            _failedRoots.Clear();
            _running = true;

            var cts = new CancellationTokenSource();
            _cts = cts;
            _loop = Task.Run(() => RunAsync(cts.Token));
        }

        // Стартовая синхронизация - сразу, не дожидаясь первого тика
        SyncWatchers();
    }

    /// <summary>Останавливает наблюдение. Идемпотентно. После Stop события не эмитятся.</summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            _running = false;
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

        lock (_gate)
        {
            foreach (var watcher in _watchers.Values)
            {
                DisposeWatcher(watcher);
            }

            _watchers.Clear();
            _failedRoots.Clear();
        }
    }

    public void Dispose() => Stop();

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_syncInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    SyncWatchers();
                    CleanupDedupCache();
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

    /// <summary>Сверяет набор наблюдателей с реестром: новые корни - добавить, исчезнувшие - dispose, упавшие - пересоздать.</summary>
    private void SyncWatchers()
    {
        IReadOnlyCollection<string> roots;
        try
        {
            roots = _registry.RegisteredRoots;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось прочитать реестр проектов, тик сверки пропущен");
            return;
        }

        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            // Исчезнувшие из реестра корни - наблюдатели на dispose
            foreach (var known in _watchers.Keys.ToArray())
            {
                if (!roots.Contains(known))
                {
                    DisposeWatcher(known);
                }
            }

            // Упавшие (Error) - снимаем; нижележащий цикл добавления пересоздаст
            foreach (var failed in _failedRoots.ToArray())
            {
                DisposeWatcher(failed);
                _failedRoots.Remove(failed);
            }

            // Новые корни - наблюдатель на корень
            foreach (var root in roots)
            {
                if (_watchers.ContainsKey(root) || !Directory.Exists(root))
                {
                    continue; // уже наблюдается или корень удалён с диска - ждём
                }

                var watcher = TryCreateWatcher(root);
                if (watcher is not null)
                {
                    _watchers[root] = watcher;
                }
            }
        }
    }

    private FileSystemWatcher? TryCreateWatcher(string root)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                InternalBufferSize = InternalBufferSizeBytes,
                EnableRaisingEvents = false, // включится после подписки обработчиков
            };
            watcher.Created += (_, e) => OnFileEvent(root, e.FullPath, FileChangeKind.Added);
            watcher.Changed += (_, e) => OnFileEvent(root, e.FullPath, FileChangeKind.Modified);
            watcher.Deleted += (_, e) => OnFileEvent(root, e.FullPath, FileChangeKind.Deleted);
            watcher.Renamed += (_, e) => OnFileEvent(root, e.FullPath, FileChangeKind.Modified);
            watcher.Error += (_, e) => OnWatcherError(root, e.GetException());
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось создать наблюдатель для корня {Root}", root);
            watcher?.Dispose();
            return null;
        }
    }

    /// <summary>Общий обработчик событий watcher'а. Никогда не бросает наружу.</summary>
    private void OnFileEvent(string root, string fullPath, FileChangeKind kind)
    {
        if (!_running)
        {
            return;
        }

        try
        {
            if (string.IsNullOrEmpty(fullPath) || HasExcludedSegment(root, fullPath))
            {
                return;
            }

            // Каталоги (создание папки, её переименование) - не файловая активность.
            // Для Deleted проверка не сработает (пути уже нет) - такое событие проходит
            if (Directory.Exists(fullPath))
            {
                return;
            }

            var now = _clock.UtcNow;
            var key = (fullPath.ToLowerInvariant(), kind);
            if (_recentEmissions.TryGetValue(key, out var lastEmitted) && now - lastEmitted < DedupWindow)
            {
                return; // то же (path, change_kind) в пределах окна - дубликат
            }

            _recentEmissions[key] = now;

            Emit(new Observation(
                new Observable(
                    Kind: ObservableKind.FilePath,
                    Value: fullPath,
                    ProcessId: null,
                    ApplicationName: null,
                    ApplicationExePath: null,
                    Timestamp: now,
                    ProjectRootPath: root),
                EventKind.FileChanged,
                FileChange: kind,
                FileSource: FileActivitySource.Watcher));
        }
        catch
        {
            // Исключения обработчика не должны утекать в FileSystemWatcher
        }
    }

    /// <summary>
    /// Error watcher'а (обычно переполнение внутреннего буфера): лог и пометка
    /// корня на пересоздание. Сам наблюдатель здесь не dispose'им - это его
    /// собственный поток обратного вызова; пересоздание делает тик сверки.
    /// </summary>
    private void OnWatcherError(string root, Exception? exception)
    {
        if (!_running)
        {
            return;
        }

        _logger.LogWarning(exception, "Ошибка наблюдателя корня {Root} (вероятно, переполнен буфер) - наблюдатель будет пересоздан", root);
        lock (_gate)
        {
            if (_watchers.ContainsKey(root))
            {
                _failedRoots.Add(root);
            }
        }
    }

    /// <summary>
    /// Относительный путь внутри проекта содержит исключённый каталог как
    /// сегмент (любой уровень). Сегменты пути ДО корня (например, AppData
    /// в %LOCALAPPDATA%, где может лежать сам проект) не заглушают проект.
    /// </summary>
    private static bool HasExcludedSegment(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (ExcludedDirectories.Contains(segment))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Вычищает записи дедуп-кэша старше ~1 минуты, чтобы кэш не разрастался.</summary>
    private void CleanupDedupCache()
    {
        var threshold = _clock.UtcNow - DedupEntryTtl;
        foreach (var pair in _recentEmissions)
        {
            if (pair.Value < threshold)
            {
                _recentEmissions.TryRemove(pair.Key, out _);
            }
        }
    }

    private void DisposeWatcher(string root)
    {
        if (_watchers.Remove(root, out var watcher))
        {
            DisposeWatcher(watcher);
        }
    }

    private void DisposeWatcher(FileSystemWatcher watcher)
    {
        try
        {
            watcher.Dispose();
        }
        catch
        {
            // Наблюдатель мог быть в рассинхронизированном состоянии после Error
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
