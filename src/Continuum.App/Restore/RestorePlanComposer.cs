using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Continuum.Restore;

/// <summary>
/// Входные данные плана восстановления, собранные из фактов сессии
/// (ФТ-6.1: «приложение - окно - файлы»): хосты для запуска и набор
/// файлов, которые хосты откроют сами (их шагами OpenFile дублировать нельзя).
/// </summary>
public sealed record ComposedPlanInputs(
    IReadOnlyList<RestoreHostApp> Hosts,
    IReadOnlySet<string> FilesCoveredByHosts);

/// <summary>
/// Собирает хосты восстановления из фактов, а не из угадывания заголовков:
/// - приложения сессии (по событиям с ApplicationId) - по имени процесса;
/// - открытые файлы последнего снапшота с позицией курсора (source
///   vscode_state, Line != null) - хостом «code -r -g путь:строка»: VS Code
///   откроет файл на той же строке (readme, шаг 7);
/// - если code на машине нет, шаг честно упадёт с «приложение не найдено»,
///   а файлы останутся в плане обычными OpenFile (hosts не покрывают их,
///   когда адаптер не доступен - проверяется через IApplicationResolver).
/// Никогда не бросает: сбой сбора даёт пустой набор, план строится как раньше.
/// </summary>
public sealed class RestorePlanComposer
{
    /// <summary>Сколько файлов открывать с позицией строки (последний экран работы).</summary>
    private const int MaxCodeFileSteps = 5;

    private readonly IRepository _repository;
    private readonly IApplicationResolver _resolver;
    private readonly ILogger<RestorePlanComposer> _logger;

    public RestorePlanComposer(
        IRepository repository,
        IApplicationResolver resolver,
        ILogger<RestorePlanComposer> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Собирает хосты и покрытые ими файлы для сессии.</summary>
    public async Task<ComposedPlanInputs> ComposeAsync(long sessionId, CancellationToken ct = default)
    {
        try
        {
            var hosts = new List<RestoreHostApp>();
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await AddApplicationHostsAsync(sessionId, hosts, ct).ConfigureAwait(false);
            await AddEditorHostsAsync(sessionId, hosts, covered, ct).ConfigureAwait(false);

            return new ComposedPlanInputs(hosts, covered);
        }
        catch (OperationCanceledException)
        {
            return new ComposedPlanInputs([], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось собрать хосты плана - план без хостов");
            return new ComposedPlanInputs([], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>Приложения, с которыми работал пользователь: по имени процесса, без дублей.</summary>
    private async Task AddApplicationHostsAsync(
        long sessionId,
        List<RestoreHostApp> hosts,
        CancellationToken ct)
    {
        var events = await _repository.GetSessionEventsAsync(sessionId, ct).ConfigureAwait(false);
        var applicationIds = events
            .Where(e => e.ApplicationId is not null)
            .Select(e => e.ApplicationId!.Value)
            .Distinct()
            .ToList();

        foreach (var applicationId in applicationIds)
        {
            var application = await _repository.GetApplicationAsync(applicationId, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(application?.ExePath))
            {
                continue; // без exe-пути запускать нечего
            }

            var processName = Path.GetFileNameWithoutExtension(application.ExePath);
            if (string.IsNullOrWhiteSpace(processName))
            {
                continue;
            }

            if (hosts.Any(h => string.Equals(h.ProcessName, processName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            hosts.Add(new RestoreHostApp(processName, null));
        }
    }

    /// <summary>Файлы с позицией курсора - хостом code -r -g «путь:строка».</summary>
    private async Task AddEditorHostsAsync(
        long sessionId,
        List<RestoreHostApp> hosts,
        HashSet<string> covered,
        CancellationToken ct)
    {
        // Без code на машине хост-шаги бессмысленны: файлы пойдут обычным OpenFile
        if (_resolver.ResolveExecutable("code") is null)
        {
            return;
        }

        var snapshot = await _repository.GetLastSnapshotAsync(sessionId, ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            return;
        }

        var openFiles = await _repository.GetSnapshotOpenFilesAsync(snapshot.Id, ct).ConfigureAwait(false);
        var withLine = openFiles
            .Where(f => f.Line is not null && !string.IsNullOrWhiteSpace(f.Path) && File.Exists(f.Path))
            .Take(MaxCodeFileSteps)
            .ToList();

        foreach (var file in withLine)
        {
            hosts.Add(new RestoreHostApp("code", $"-r -g \"{file.Path}:{file.Line}\""));
            covered.Add(file.Path);
        }
    }
}
