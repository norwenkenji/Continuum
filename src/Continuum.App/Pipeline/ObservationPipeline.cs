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
    private readonly ILogger<ObservationPipeline> _logger;
    private readonly List<IObservationSource> _attached = [];

    public ObservationPipeline(
        IPrivacyFilter filter,
        ISanitizer sanitizer,
        IRepository repository,
        ISessionContext session,
        ILogger<ObservationPipeline> logger)
    {
        _filter = filter;
        _sanitizer = sanitizer;
        _repository = repository;
        _session = session;
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

            // 3. Persist: приложение → справочник, наблюдение → событие хронологии
            long? applicationId = null;
            if (!string.IsNullOrWhiteSpace(o.ApplicationName))
            {
                applicationId = await _repository.UpsertApplicationAsync(
                    new ApplicationInfo(0, o.ApplicationName!, o.ApplicationExePath, null, o.Timestamp, o.Timestamp),
                    ct).ConfigureAwait(false);
            }

            var ev = new Event(
                Id: 0,
                SessionId: _session.CurrentSessionId,
                Ts: o.Timestamp,
                Kind: observation.EventKind,
                ApplicationId: applicationId,
                ProjectId: null,
                Title: o.Kind == ObservableKind.WindowTitle ? value : null,
                Path: o.Kind is ObservableKind.FilePath or ObservableKind.Cwd ? value : null,
                Url: o.Kind == ObservableKind.Url ? value : null,
                MetaJson: o.Kind == ObservableKind.CommandLine && value is not null
                    ? JsonSerializer.Serialize(new { cmd = value })
                    : null);

            await _repository.AppendEventAsync(ev, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка обработки наблюдения {EventKind}", observation.EventKind);
        }
    }
}
