using System.Text.Json;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Core.Interfaces;
using Continuum.Runtime;
using Microsoft.Extensions.Logging;

namespace Continuum.Snapshots;

/// <summary>
/// Снапшоттер (шаг 4): снимает точку состояния контекста.
/// - по таймеру (каждые 5 минут, reason=timer);
/// - по явному вызову SaveSnapshotAsync (session_end/shutdown - из
///   SessionSupervisor и App.OnExit/OnSessionEnding).
///
/// Состав снапшота: git_state по каждому зарегистрированному проекту.
/// Никогда не бросает: проект без git пишется с available=0 (критерий readme),
/// сбой проекта не отменяет снапшот остальных.
/// </summary>
public sealed class Snapshotter : ISnapshotter, IDisposable
{
    /// <summary>Период фонового снапшота.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    /// <summary>Версия формата summary_json (readme: версионируется отдельно от схемы БД).</summary>
    private const int CurrentSchemaVersion = 1;

    private readonly IRepository _repository;
    private readonly ISessionContext _session;
    private readonly IProjectRegistry _projects;
    private readonly IGitClient _git;
    private readonly IPrivacyFilter _privacy;
    private readonly IClock _clock;
    private readonly ILogger<Snapshotter> _logger;
    private readonly TimeSpan _interval;
    private readonly IOpenFilesProvider? _openFiles;
    private readonly IProjectResolver? _projectResolver;
    private readonly ITerminalsProvider? _terminals;
    private readonly IBrowserTabsProvider? _browserTabs;
    private readonly ISanitizer? _sanitizer;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public Snapshotter(
        IRepository repository,
        ISessionContext session,
        IProjectRegistry projects,
        IGitClient git,
        IPrivacyFilter privacy,
        IClock clock,
        ILogger<Snapshotter> logger,
        TimeSpan? interval = null,
        IOpenFilesProvider? openFiles = null,
        IProjectResolver? projectResolver = null,
        ITerminalsProvider? terminals = null,
        IBrowserTabsProvider? browserTabs = null,
        ISanitizer? sanitizer = null)
    {
        _repository = repository;
        _session = session;
        _projects = projects;
        _git = git;
        _privacy = privacy;
        _clock = clock;
        _logger = logger;
        _interval = interval ?? DefaultInterval;
        _openFiles = openFiles;
        _projectResolver = projectResolver;
        _terminals = terminals;
        _browserTabs = browserTabs;
        _sanitizer = sanitizer;
    }

    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // выход приложения - не мешаем
        }

        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    public void Dispose()
    {
        Stop();
        _saveLock.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await SaveSnapshotAsync(SnapshotReason.Timer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Сбой фонового снапшота - продолжаю");
            }
        }
    }

    public async Task<long> SaveSnapshotAsync(SnapshotReason reason, CancellationToken ct = default)
    {
        await _saveLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sessionId = _session.CurrentSessionId;
            if (sessionId == 0)
            {
                _logger.LogWarning("Снапшот без активной сессии пропущен ({Reason})", reason);
                return 0;
            }

            var now = _clock.UtcNow;
            var gitStates = new List<GitState>();

            foreach (var root in _projects.RegisteredRoots)
            {
                var projectId = _projects.GetProjectId(root);
                if (projectId is null)
                {
                    continue; // корень есть, но проект ещё не upsert-нут - пропускаем
                }

                try
                {
                    gitStates.Add(await BuildGitStateAsync(projectId.Value, root, ct).ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    // available=0 - норма, не исключение; сюда попадают только сбои
                    _logger.LogWarning(ex, "Не удалось снять git_state для {Root}", root);
                    gitStates.Add(new GitState(0, 0, projectId.Value, false, null, null, null, null, 0, null, 0));
                }
            }

            var openFiles = await CollectOpenFilesAsync(now, ct).ConfigureAwait(false);
            var terminalStates = await CollectTerminalsAsync(now, ct).ConfigureAwait(false);
            var browserTabs = await CollectBrowserTabsAsync(now, ct).ConfigureAwait(false);

            var snapshot = new Snapshot(0, sessionId, now, reason, CurrentSchemaVersion, BuildSummaryJson(gitStates))
            {
                GitStates = gitStates,
                OpenFiles = openFiles,
                Terminals = terminalStates,
                BrowserTabs = browserTabs,
            };
            var id = await _repository.SaveSnapshotAsync(snapshot, ct).ConfigureAwait(false);
            _logger.LogDebug("Снапшот {Id} ({Reason}): {Count} проектов", id, reason, gitStates.Count);
            return id;
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// Открытые файлы для снапшота (шаг 7): провайдер опрашивается на момент
    /// снапшота; пути проходят privacy-фильтр (как dirty-файлы), проект
    /// определяется через резолвер git root. Провайдера нет или он недоступен -
    /// снапшот без open_files (честная деградация).
    /// </summary>
    private async Task<List<OpenFile>> CollectOpenFilesAsync(DateTimeOffset now, CancellationToken ct)
    {
        var result = new List<OpenFile>();
        if (_openFiles is null)
        {
            return result;
        }

        try
        {
            foreach (var file in await _openFiles.GetOpenFilesAsync(ct).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(file.Path))
                {
                    continue;
                }

                var probe = new Observable(ObservableKind.FilePath, file.Path, null, null, null, now, null);
                if (!_privacy.Allow(probe))
                {
                    continue;
                }

                long? projectId = null;
                if (_projectResolver is not null)
                {
                    var root = _projectResolver.ResolveRoot(file.Path);
                    if (root is not null)
                    {
                        projectId = _projects.GetProjectId(root);
                    }
                }

                result.Add(file with { Id = 0, SnapshotId = 0, ProjectId = projectId });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось собрать открытые файлы - снапшот без open_files");
        }

        return result;
    }

    /// <summary>
    /// Терминалы для снапшота (шаг 10): провайдер опрашивается на момент
    /// снапшота; Cwd проходит privacy-фильтр (исключённые каталоги и правила),
    /// заголовок-хост - проба приложения (исключённый браузер терминала
    /// не пишется). Провайдера нет или терминалов нет - снапшот без terminals.
    /// </summary>
    private async Task<List<TerminalState>> CollectTerminalsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var result = new List<TerminalState>();
        if (_terminals is null)
        {
            return result;
        }

        try
        {
            foreach (var terminal in await _terminals.GetTerminalsAsync(ct).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(terminal.Host))
                {
                    continue;
                }

                // Cwd - путь: исключённые каталоги и правила «не записывать никогда»
                if (!string.IsNullOrEmpty(terminal.Cwd))
                {
                    var probe = new Observable(
                        ObservableKind.Cwd, terminal.Cwd, null, terminal.Host, null, now, null);
                    if (!_privacy.Allow(probe))
                    {
                        continue; // терминал в исключённом каталоге - не записываем строку
                    }
                }

                result.Add(terminal with { Id = 0, SnapshotId = 0 });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось собрать терминалы - снапшот без terminals");
        }

        return result;
    }

    /// <summary>
    /// Вкладки браузера для снапшота (шаг 10): заголовок и URL проходят
    /// privacy-фильтр (исключённый браузер / BY_HOST-правила - вкладка
    /// не пишется), URL - санитизация (секреты в query). Приложение браузера
    /// апсертится в справочник для application_id. URL, который не прошёл
    /// фильтр, не заменяется пустым - вкладка отбрасывается целиком
    /// (privacy-first: недозапись лучше лишней записи).
    /// </summary>
    private async Task<List<BrowserTab>> CollectBrowserTabsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var result = new List<BrowserTab>();
        if (_browserTabs is null)
        {
            return result;
        }

        try
        {
            foreach (var tab in await _browserTabs.GetBrowserTabsAsync(ct).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(tab.BrowserName))
                {
                    continue;
                }

                // Заголовок: проба окна (исключённое приложение - вкладки не пишутся)
                if (!string.IsNullOrEmpty(tab.Title))
                {
                    var titleProbe = new Observable(
                        ObservableKind.WindowTitle, tab.Title, null, tab.BrowserName, tab.ExePath, now, null);
                    if (!_privacy.Allow(titleProbe))
                    {
                        continue;
                    }
                }

                // URL: проба Url-вида - BY_HOST и BY_PATH_SUBSTR применяются
                // только при реально полученном URL (§5.4)
                string? url = null;
                if (tab.UrlAvailable && !string.IsNullOrEmpty(tab.Url))
                {
                    var urlProbe = new Observable(
                        ObservableKind.Url, tab.Url, null, tab.BrowserName, tab.ExePath, now, null);
                    if (!_privacy.Allow(urlProbe))
                    {
                        continue;
                    }

                    url = _sanitizer?.Sanitize(tab.Url, ObservableKind.Url) ?? tab.Url;
                }

                var applicationId = await _repository.UpsertApplicationAsync(
                    new ApplicationInfo(0, tab.BrowserName, tab.ExePath, null, now, now), ct).ConfigureAwait(false);

                result.Add(new BrowserTab(
                    Id: 0,
                    SnapshotId: 0,
                    ApplicationId: applicationId,
                    Url: url,
                    Title: tab.Title,
                    UrlAvailable: url is not null,
                    IsActive: tab.IsActive,
                    Source: tab.Source));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось собрать вкладки браузера - снапшот без browser_tabs");
        }

        return result;
    }

    private async Task<GitState> BuildGitStateAsync(long projectId, string root, CancellationToken ct)
    {
        var state = await _git.GetStateAsync(root, ct).ConfigureAwait(false);
        if (!state.Available)
        {
            return new GitState(0, 0, projectId, false, null, null, null, null, 0, null, 0);
        }

        // Пути dirty-файлов проходят privacy-конвейер как обычные наблюдения:
        // снапшот не обходной путь вокруг фильтра (privacy-pipeline.md)
        var visible = new List<DirtyFile>();
        foreach (var file in state.DirtyFiles)
        {
            var probe = new Observable(
                ObservableKind.FilePath, file.Path, null, null, null, _clock.UtcNow, root);
            if (_privacy.Allow(probe))
            {
                visible.Add(file);
            }
        }

        var dirtyCount = visible.Count(f => f.Kind != FileChangeKind.Untracked);
        var untrackedCount = visible.Count(f => f.Kind == FileChangeKind.Untracked);
        var dirtyJson = visible.Count == 0
            ? null
            : JsonSerializer.Serialize(visible.Select(f => new { path = f.Path, kind = f.Kind switch
                {
                    FileChangeKind.Modified => "modified",
                    FileChangeKind.Added => "added",
                    FileChangeKind.Deleted => "deleted",
                    _ => "untracked",
                } }));

        return new GitState(0, 0, projectId, true, state.Branch, state.HeadCommit,
            state.HeadSubject, state.HeadTimestamp, dirtyCount, dirtyJson, untrackedCount);
    }

    private static string BuildSummaryJson(List<GitState> gitStates)
    {
        // Реляционные таблицы - только то, по чему есть запросы; счётчики сюда
        var dirtyTotal = gitStates.Sum(g => g.DirtyCount + g.UntrackedCount);
        return JsonSerializer.Serialize(new
        {
            projects = gitStates.Count,
            gitAvailable = gitStates.Count(g => g.Available),
            dirtyTotal,
        });
    }
}
