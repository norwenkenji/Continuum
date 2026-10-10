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
        TimeSpan? interval = null)
    {
        _repository = repository;
        _session = session;
        _projects = projects;
        _git = git;
        _privacy = privacy;
        _clock = clock;
        _logger = logger;
        _interval = interval ?? DefaultInterval;
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

            var snapshot = new Snapshot(0, sessionId, now, reason, CurrentSchemaVersion, BuildSummaryJson(gitStates))
            {
                GitStates = gitStates,
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
