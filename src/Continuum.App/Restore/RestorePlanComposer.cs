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
/// (ФТ-6.1: «приложение - окно - файлы»): хосты для запуска, набор
/// файлов, которые хосты откроют сами (их шагами OpenFile дублировать нельзя),
/// и URL вкладок для шагов OpenUrl (когда хост браузера не собирается).
/// </summary>
public sealed record ComposedPlanInputs(
    IReadOnlyList<RestoreHostApp> Hosts,
    IReadOnlySet<string> FilesCoveredByHosts,
    IReadOnlyList<string> OpenUrls);

/// <summary>
/// Собирает хосты восстановления из фактов, а не из угадывания заголовков:
/// - приложения сессии (по событиям с ApplicationId) - по имени процесса;
/// - открытые файлы последнего снапшота с позицией курсора (source
///   vscode_state, Line != null) - хостом «code -r -g путь:строка»: VS Code
///   откроет файл на той же строке (readme, шаг 7);
/// - вкладки браузера последнего снапшота с URL (§5.6): если chrome
///   резолвится - один хост со всеми URL (chrome url1 url2...); нет -
///   URL остаются шагами OpenUrl через браузер по умолчанию.
/// Никогда не бросает: сбой сбора даёт пустой набор, план строится как раньше.
/// </summary>
public sealed class RestorePlanComposer
{
    /// <summary>Сколько файлов открывать с позицией строки (последний экран работы).</summary>
    private const int MaxCodeFileSteps = 5;

    /// <summary>Сколько URL вкладок попадает в план (последний экран работы).</summary>
    private const int MaxUrlSteps = 5;

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

    /// <summary>Собирает хосты, покрытые файлы и URL вкладок для сессии.</summary>
    public async Task<ComposedPlanInputs> ComposeAsync(long sessionId, CancellationToken ct = default)
    {
        var emptyUrls = new List<string>();
        try
        {
            var hosts = new List<RestoreHostApp>();
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await AddApplicationHostsAsync(sessionId, hosts, ct).ConfigureAwait(false);
            await AddEditorHostsAsync(sessionId, hosts, covered, ct).ConfigureAwait(false);
            var openUrls = await AddBrowserUrlsAsync(sessionId, hosts, ct).ConfigureAwait(false);

            return new ComposedPlanInputs(hosts, covered, openUrls);
        }
        catch (OperationCanceledException)
        {
            return new ComposedPlanInputs([], new HashSet<string>(StringComparer.OrdinalIgnoreCase), emptyUrls);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось собрать хосты плана - план без хостов");
            return new ComposedPlanInputs([], new HashSet<string>(StringComparer.OrdinalIgnoreCase), emptyUrls);
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

    /// <summary>
    /// Вкладки браузера с точным URL (§5.6): chrome резолвится - один хост
    /// со всеми URL («chrome url1 url2» открывает их одним окном; если Chrome
    /// уже запущен, шаг честно пропустится - дубликаты вкладок не плодим).
    /// Chrome нет - URL возвращаются для шагов OpenUrl через браузер
    /// по умолчанию. Возвращает список URL для шагов OpenUrl (пустой, если
    /// хост собрал URL в свои аргументы).
    /// </summary>
    private async Task<List<string>> AddBrowserUrlsAsync(
        long sessionId,
        List<RestoreHostApp> hosts,
        CancellationToken ct)
    {
        var snapshot = await _repository.GetLastSnapshotAsync(sessionId, ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            return [];
        }

        var tabs = await _repository.GetSnapshotBrowserTabsAsync(snapshot.Id, ct).ConfigureAwait(false);
        var urls = tabs
            .Where(t => t.UrlAvailable && !string.IsNullOrWhiteSpace(t.Url))
            .Select(t => t.Url!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxUrlSteps)
            .ToList();
        if (urls.Count == 0)
        {
            return [];
        }

        // Chrome найден - все URL одним хостом (§5.6); уже запущенный Chrome
        // пропустит шаг идемпотентностью - вкладки не дублируются.
        // Хост chrome от приложений сессии получает URL в свои аргументы
        if (_resolver.ResolveExecutable("chrome") is not null)
        {
            var arguments = string.Join(" ", urls.Select(u => $"\"{u}\""));
            var existing = hosts.FindIndex(h => string.Equals(h.ProcessName, "chrome", StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                hosts[existing] = hosts[existing] with { Arguments = arguments };
            }
            else
            {
                hosts.Add(new RestoreHostApp("chrome", arguments));
            }

            return [];
        }

        // Chrome не резолвится: URL откроются браузером по умолчанию
        return urls;
    }
}
