using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Continuum.Adapters.VSCode;

/// <summary>
/// Адаптер VS Code (data-sources §7): факты из локальных хранилищ, а не заголовки окон.
/// Две роли:
/// - IObservationSource: цикл опроса User\History (по умолчанию раз в 45 с,
///   §0.3 - 30-60 с) эмитит FileChanged/Modified/VsCodeHistory на новое
///   сохранение файла;
/// - IOpenFilesProvider: GetOpenFilesAsync читает открытые файлы с позициями
///   курсора из workspaceStorage\state.vscdb (workbench.state) по требованию -
///   вызывается Snapshotter'ом на момент снапшота, не в цикле опроса; нет данных
///   workspace - fallback на недавние файлы из globalStorage.
/// Корни задаются списком (Code, Code - Insiders, VSCodium, Cursor - §7.2),
/// не хардкодом одного. Класс источника B: недоступность корня или битые файлы -
/// тихий пропуск, адаптер никогда не бросает и не роняет сессию.
/// </summary>
public sealed class VSCodeAdapter : IObservationSource, IOpenFilesProvider, IDisposable
{
    /// <summary>Интервал опроса по умолчанию (data-sources §0.3: чтение state.vscdb / entries.json - 30-60 с).</summary>
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(45);

    /// <summary>Метка источника в open_files (source = 'vscode_state', §7).</summary>
    private const string OpenFileSource = "vscode_state";

    private readonly IClock _clock;
    private readonly ILogger<VSCodeAdapter> _logger;
    private readonly IReadOnlyList<string> _roots;
    private readonly IProjectResolver? _resolver;
    private readonly TimeSpan _pollInterval;
    private readonly object _gate = new();

    /// <summary>Путь файла -&gt; время последнего сохранения из History (null - entries пуст).</summary>
    private readonly Dictionary<string, DateTimeOffset?> _lastSaveByPath = new(StringComparer.OrdinalIgnoreCase);

    private bool _baselineTaken;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public VSCodeAdapter(
        IClock clock,
        ILogger<VSCodeAdapter> logger,
        IReadOnlyList<string>? roots = null,
        IProjectResolver? resolver = null,
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
        _roots = roots ?? DefaultRoots();
        _resolver = resolver;
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    /// <summary>Корни по умолчанию (§7.2): VS Code, Insiders, VSCodium, Cursor в %APPDATA%.</summary>
    private static IReadOnlyList<string> DefaultRoots()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData))
        {
            return Array.Empty<string>();
        }

        return
        [
            Path.Combine(appData, "Code"),
            Path.Combine(appData, "Code - Insiders"),
            Path.Combine(appData, "VSCodium"),
            Path.Combine(appData, "Cursor"),
        ];
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

            // Перезапуск - новая точка отсчёта: база снимается заново
            _lastSaveByPath.Clear();
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
                    // Любая ошибка тика - пропускаем тик, адаптер продолжает работу
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Штатная остановка через Stop
        }
    }

    /// <summary>
    /// Один тик опроса: снимок History по всем корням и дифф с прошлым тиком.
    /// Первый тик - базовый, молча (§0.4: событие на изменение, не на тик).
    /// Дальше эмитится только новое сохранение: файл появился или время
    /// последнего сохранения стало позже. Пустые entries (время неизвестно)
    /// в событиях не участвуют - путь валиден только для open_files.
    /// </summary>
    private void Tick()
    {
        var snapshot = new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _roots)
        {
            try
            {
                foreach (var entry in VSCodeStateReader.ReadHistory(root))
                {
                    snapshot[entry.Path] = entry.LastSave;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Чтение History корня {Root} не удалось, корень пропущен", root);
            }
        }

        List<string>? changedPaths = null;
        lock (_gate)
        {
            if (!_baselineTaken)
            {
                foreach (var pair in snapshot)
                {
                    _lastSaveByPath[pair.Key] = pair.Value;
                }

                _baselineTaken = true;
                return;
            }

            foreach (var pair in snapshot)
            {
                var known = _lastSaveByPath.TryGetValue(pair.Key, out var previous);
                _lastSaveByPath[pair.Key] = pair.Value;
                var isNewSave = pair.Value is not null
                    && (!known || previous is null || pair.Value > previous);
                if (isNewSave)
                {
                    (changedPaths ??= new List<string>()).Add(pair.Key);
                }
            }
        }

        if (changedPaths is null)
        {
            return;
        }

        foreach (var path in changedPaths)
        {
            Emit(BuildObservation(path));
        }
    }

    /// <summary>
    /// Открытые файлы на текущий момент (open_files, §7): из workspaceStorage\state.vscdb
    /// (workbench.state: resource + selectionStartLineNumber), LastModified - из
    /// History (max mtime; нет файла в History - null). Ничего из workspaceStorage -
    /// fallback на недавние файлы из globalStorage (Line = null). Дедуп по пути
    /// (OrdinalIgnoreCase). Пустой список - источник недоступен, это не ошибка;
    /// исключения наружу не выходят (контракт IOpenFilesProvider).
    /// </summary>
    public async Task<IReadOnlyList<OpenFile>> GetOpenFilesAsync(CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() => CollectOpenFiles(ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<OpenFile>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Сбор открытых файлов VS Code не удался - снапшот без open_files");
            return Array.Empty<OpenFile>();
        }
    }

    private IReadOnlyList<OpenFile> CollectOpenFiles(CancellationToken ct)
    {
        var result = new List<OpenFile>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lastModifiedByPath = new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);

        // LastModified для открытых файлов берётся из History (max mtime сохранений)
        foreach (var root in _roots)
        {
            try
            {
                foreach (var entry in VSCodeStateReader.ReadHistory(root))
                {
                    lastModifiedByPath[entry.Path] = entry.LastSave;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Чтение History корня {Root} для LastModified не удалось", root);
            }
        }

        var workspaceFound = false;
        foreach (var root in _roots)
        {
            if (ct.IsCancellationRequested)
            {
                return result;
            }

            var workspaceStorage = Path.Combine(root, "User", "workspaceStorage");
            if (!Directory.Exists(workspaceStorage))
            {
                continue;
            }

            foreach (var hashDir in VSCodeStateReader.EnumerateDirectoriesSafe(workspaceStorage))
            {
                if (ct.IsCancellationRequested)
                {
                    return result;
                }

                var stateDb = Path.Combine(hashDir, "state.vscdb");
                if (!File.Exists(stateDb))
                {
                    continue;
                }

                foreach (var line in VSCodeStateReader.ReadWorkbenchState(stateDb))
                {
                    var path = VSCodePathUtil.TryUriToLocalPath(line.Uri);
                    if (path is null || !seenPaths.Add(path))
                    {
                        continue;
                    }

                    workspaceFound = true;
                    lastModifiedByPath.TryGetValue(path, out var lastModified);
                    result.Add(new OpenFile(
                        Id: 0,
                        SnapshotId: 0,
                        Path: path,
                        ProjectId: null,
                        ApplicationId: null,
                        Line: line.Line,
                        LastModified: lastModified,
                        Source: OpenFileSource));
                }
            }
        }

        if (workspaceFound)
        {
            return result;
        }

        // Fallback (§7.1): workspaceStorage ничего не дал - недавние файлы
        // из globalStorage (позиции курсора для них нет)
        foreach (var root in _roots)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            var stateDb = Path.Combine(root, "User", "globalStorage", "state.vscdb");
            if (!File.Exists(stateDb))
            {
                continue;
            }

            foreach (var uri in VSCodeStateReader.ReadRecentlyOpenedFileUris(stateDb))
            {
                var path = VSCodePathUtil.TryUriToLocalPath(uri);
                if (path is null || !seenPaths.Add(path))
                {
                    continue;
                }

                lastModifiedByPath.TryGetValue(path, out var lastModified);
                result.Add(new OpenFile(
                    Id: 0,
                    SnapshotId: 0,
                    Path: path,
                    ProjectId: null,
                    ApplicationId: null,
                    Line: null,
                    LastModified: lastModified,
                    Source: OpenFileSource));
            }
        }

        return result;
    }

    private Observation BuildObservation(string path)
    {
        string? projectRoot = null;
        if (_resolver is not null)
        {
            try
            {
                projectRoot = _resolver.ResolveRoot(path);
            }
            catch
            {
                projectRoot = null; // резолвер упал - событие без проекта, а не без события
            }
        }

        return new Observation(
            new Observable(
                Kind: ObservableKind.FilePath,
                Value: path,
                ProcessId: null,
                ApplicationName: null,
                ApplicationExePath: null,
                Timestamp: _clock.UtcNow,
                ProjectRootPath: projectRoot),
            EventKind.FileChanged,
            FileChange: FileChangeKind.Modified,
            FileSource: FileActivitySource.VsCodeHistory);
    }

    /// <summary>Эмитит наблюдение: ошибка одного подписчика не ломает остальных и сам адаптер.</summary>
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
