using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Continuum.Stopped;

/// <summary>
/// Движок «Где я остановился?» - детерминированная таблица истинности
/// из readme (без LLM): последний проект сессии, последний файл, живое
/// состояние git. Никогда не бросает: любой сбой БД или git - Unclear.
/// </summary>
public sealed class StoppedEngine
{
    private readonly IRepository _repository;
    private readonly IGitClient _git;
    private readonly ILogger<StoppedEngine> _logger;

    public StoppedEngine(IRepository repository, IGitClient git, ILogger<StoppedEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _git = git;
        _logger = logger;
    }

    /// <summary>
    /// Оценивает сессию: по умолчанию последнюю, иначе заданную sessionId.
    /// Вердикт и факты, из которых он выведен (факты nullable - при Unclear
    /// их может не быть вовсе).
    /// </summary>
    public async Task<StoppedVerdict> EvaluateAsync(long? sessionId = null, CancellationToken ct = default)
    {
        try
        {
            return await EvaluateCoreAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Сбой оценки «Где я остановился?» - вердикт Unclear");
            return Unclear();
        }
    }

    private async Task<StoppedVerdict> EvaluateCoreAsync(long? sessionId, CancellationToken ct)
    {
        long effectiveSessionId;
        if (sessionId is { } explicitId)
        {
            effectiveSessionId = explicitId;
        }
        else
        {
            var lastSession = await _repository.GetLastSessionAsync(ct).ConfigureAwait(false);
            if (lastSession is null)
            {
                return Unclear(); // сессий ещё не было
            }

            effectiveSessionId = lastSession.Id;
        }

        var events = await _repository.GetSessionEventsAsync(effectiveSessionId, ct).ConfigureAwait(false);
        if (events.Count == 0)
        {
            return Unclear(); // сессия без событий
        }

        // Последний проект сессии - проект последнего события с ProjectId
        var lastProjectEvent = events.LastOrDefault(e => e.ProjectId is not null);
        if (lastProjectEvent is null)
        {
            return Unclear(); // проектных событий в сессии не было
        }

        var projectId = lastProjectEvent.ProjectId!.Value;
        var project = await _repository.GetProjectAsync(projectId, ct).ConfigureAwait(false);
        if (project is null)
        {
            return Unclear(); // запись проекта потеряна
        }

        // Последний файл проекта за сессию; может отсутствовать - это не Unclear
        var activities = await _repository.GetSessionFileActivityAsync(effectiveSessionId, ct).ConfigureAwait(false);
        var lastFilePath = activities.LastOrDefault(a => a.ProjectId == projectId)?.Path;

        // Живое состояние репозитория
        var state = await _git.GetStateAsync(project.RootPath, ct).ConfigureAwait(false);
        if (!state.Available)
        {
            // Git недоступен - не ошибка, а другой сценарий (readme);
            // факты о проекте и файле при этом заполняем
            return new StoppedVerdict(
                StoppedStatus.Unclear,
                project.Id,
                project.Name,
                project.RootPath,
                lastFilePath,
                Branch: null,
                HeadCommit: null,
                HeadSubject: null,
                HeadTs: null,
                DirtyCount: 0);
        }

        var dirtyCount = state.DirtyFiles.Count;

        // Таблица истинности (readme), порядок веток значим
        StoppedStatus status;
        if (dirtyCount > 0)
        {
            status = StoppedStatus.MaybeUnfinished; // есть незакоммиченное
        }
        else if (lastProjectEvent.Kind == EventKind.FileChanged)
        {
            status = StoppedStatus.MaybeUnfinished; // последнее событие проекта - правка файла
        }
        else if (lastProjectEvent.Kind == EventKind.GitCommit)
        {
            status = StoppedStatus.Completed; // чисто и последнее событие - коммит
        }
        else
        {
            // Четвёртая ветка - дополнение для тотальности: readme покрывает
            // три исхода; здесь «чисто, но последнее событие ни правка,
            // ни коммит» (например, AppFocused) - честный Unclear
            status = StoppedStatus.Unclear;
        }

        return new StoppedVerdict(
            status,
            project.Id,
            project.Name,
            project.RootPath,
            lastFilePath,
            state.Branch,
            state.HeadCommit,
            state.HeadSubject,
            state.HeadTimestamp,
            dirtyCount);
    }

    private static StoppedVerdict Unclear() => new(
        StoppedStatus.Unclear,
        ProjectId: null,
        ProjectName: null,
        ProjectRoot: null,
        LastFilePath: null,
        Branch: null,
        HeadCommit: null,
        HeadSubject: null,
        HeadTs: null,
        DirtyCount: 0);
}
