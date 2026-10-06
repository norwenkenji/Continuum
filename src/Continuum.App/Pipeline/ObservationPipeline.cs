using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Core.Interfaces;
using Continuum.Runtime;
using Microsoft.Extensions.Logging;

namespace Continuum.Pipeline;

/// <summary>
/// Privacy-конвейер как код: Observable → Filter → Sanitize → Persist
/// (docs/specs/privacy-pipeline.md, §2). Ни одна запись в БД не минует
/// этот путь: коллекторы пишут только через HandleAsync.
///
/// Надёжность: одно плохое наблюдение или сбой БД не роняют приложение -
/// ошибка логируется, конвейер продолжает работу.
/// </summary>
public sealed class ObservationPipeline
{
    private readonly IPrivacyFilter _filter;
    private readonly ISanitizer _sanitizer;
    private readonly IRepository _repository;
    private readonly ISessionContext _session;
    private readonly IProjectRegistry _projects;
    private readonly ILogger<ObservationPipeline> _logger;
    private readonly List<IObservationSource> _attached = [];

    public ObservationPipeline(
        IPrivacyFilter filter,
        ISanitizer sanitizer,
        IRepository repository,
        ISessionContext session,
        IProjectRegistry projects,
        ILogger<ObservationPipeline> logger)
    {
        _filter = filter;
        _sanitizer = sanitizer;
        _repository = repository;
        _session = session;
        _projects = projects;
        _logger = logger;
    }

    /// <summary>Подписывает источник на конвейер.</summary>
    public void Attach(IObservationSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.Observed += OnObserved;
        _attached.Add(source);
    }

    /// <summary>Отписывает все источники (при остановке).</summary>
    public void DetachAll()
    {
        foreach (var source in _attached)
        {
            source.Observed -= OnObserved;
        }

        _attached.Clear();
    }

    // async void допустим: HandleAsync не бросает исключений наружу
    private async void OnObserved(object? sender, Observation observation)
        => await HandleAsync(observation).ConfigureAwait(false);

    /// <summary>Прогоняет одно наблюдение через конвейер. Никогда не бросает.</summary>
    public async Task HandleAsync(Observation observation, CancellationToken ct = default)
    {
        try
        {
            var o = observation.Observable;

            // 1. Filter: false - отбросить целиком, ничего не записываем
            if (!_filter.Allow(o))
            {
                return;
            }

            // 2. Sanitize: секреты маскируются до записи
            var value = o.Value is null ? null : _sanitizer.Sanitize(o.Value, o.Kind);

            // 3. Persist: приложение и проект → справочники, наблюдение → хронология
            long? applicationId = null;
            if (!string.IsNullOrWhiteSpace(o.ApplicationName))
            {
                applicationId = await _repository.UpsertApplicationAsync(
                    new ApplicationInfo(0, o.ApplicationName!, o.ApplicationExePath, null, o.Timestamp, o.Timestamp),
                    ct).ConfigureAwait(false);
            }

            long? projectId = null;
            if (!string.IsNullOrWhiteSpace(o.ProjectRootPath))
            {
                // Реестр сам решает: кэш или upsert; last_seen обновляется
                // при первой регистрации корня за сессию (не на каждое событие)
                projectId = await _projects.RegisterPathAsync(o.ProjectRootPath!, ct).ConfigureAwait(false);
            }

            var ev = new Event(
                Id: 0,
                SessionId: _session.CurrentSessionId,
                Ts: o.Timestamp,
                Kind: observation.EventKind,
                ApplicationId: applicationId,
                ProjectId: projectId,
                Title: o.Kind == ObservableKind.WindowTitle ? value : null,
                Path: o.Kind is ObservableKind.FilePath or ObservableKind.Cwd ? value : null,
                Url: o.Kind == ObservableKind.Url ? value : null,
                MetaJson: o.Kind == ObservableKind.CommandLine && value is not null
                    ? JsonSerializer.Serialize(new { cmd = value })
                    : null);

            await _repository.AppendEventAsync(ev, ct).ConfigureAwait(false);

            // Активность файлов: «Файлы за сессию» - event-уровень (readme, «Схема БД»)
            if (observation.EventKind is EventKind.FileChanged or EventKind.FileOpened)
            {
                if (observation.FileChange is null || observation.FileSource is null || value is null)
                {
                    _logger.LogWarning(
                        "Файловое событие {EventKind} без FileChange/FileSource/пути - отброшено",
                        observation.EventKind);
                    return;
                }

                await _repository.AppendFileActivityAsync(
                    new FileActivity(
                        Id: 0,
                        SessionId: _session.CurrentSessionId,
                        Ts: o.Timestamp,
                        ProjectId: projectId,
                        Path: value,
                        ChangeKind: observation.FileChange.Value,
                        Source: observation.FileSource.Value),
                    ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка обработки наблюдения {EventKind}", observation.EventKind);
        }
    }
}
