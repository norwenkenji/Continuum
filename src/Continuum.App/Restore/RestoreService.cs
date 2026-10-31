using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Continuum.Restore;

/// <summary>
/// Приложение-хост восстановления: имя процесса и аргументы запуска
/// (например, «code» с «-g путь:строка»). Список передаётся извне -
/// по умолчанию пустой; MainViewModel наполнит его на следующих шагах.
/// План хранит только имя процесса, резолв пути - в момент выполнения.
/// </summary>
public sealed record RestoreHostApp(string ProcessName, string? Arguments);

/// <summary>
/// Шов проверки «процесс уже запущен» - идемпотентность повторного
/// восстановления (критерий шага 6: повтор не плодит дубликаты).
/// </summary>
public interface IRunningProcessCheck
{
    /// <summary>True - процесс с таким именем уже запущен.</summary>
    bool IsRunning(string processName);
}

/// <summary>
/// Реальная проверка через Process.GetProcessesByName - разовая, в момент
/// восстановления; это НЕ цикл мониторинга, бюджет не трогает.
/// </summary>
public sealed class RunningProcessCheck : IRunningProcessCheck
{
    /// <inheritdoc/>
    public bool IsRunning(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var baseName = processName.Trim();
        if (baseName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            baseName = baseName[..^4];
        }

        try
        {
            return Process.GetProcessesByName(baseName).Length > 0;
        }
        catch
        {
            // проверка не удалась - считаем, что не запущен: шаг честно выполнится
            return false;
        }
    }
}

/// <summary>
/// Restore Engine (readme, шаг 6): построение и выполнение плана
/// восстановления. Порядок шагов значим (хосты → файлы), падение одного
/// шага не отменяет остальные, dry-run ничего не запускает, повтор не
/// плодит дубликаты процессов. Никогда не бросает.
///
/// План - восстановление СНАПШОТА сессии: restore_plans.snapshot_id
/// NOT NULL, поэтому план без снапшота (SnapshotId = 0) не выполняется и
/// не записывается в БД - без снапшота восстанавливать нечего.
/// </summary>
public sealed class RestoreService
{
    /// <summary>Сколько последних файлов попадает в план (MVP: последний экран работы).</summary>
    private const int MaxFileSteps = 10;

    private readonly IRepository _repository;
    private readonly IApplicationResolver _resolver;
    private readonly IProcessLauncher _launcher;
    private readonly IRunningProcessCheck _runningCheck;
    private readonly IClock _clock;
    private readonly ILogger<RestoreService> _logger;

    public RestoreService(
        IRepository repository,
        IApplicationResolver resolver,
        IProcessLauncher launcher,
        IClock clock,
        ILogger<RestoreService> logger,
        IRunningProcessCheck? runningCheck = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _resolver = resolver;
        _launcher = launcher;
        _clock = clock;
        _logger = logger;
        _runningCheck = runningCheck ?? new RunningProcessCheck();
    }

    /// <summary>
    /// Строит план по последней (или заданной) сессии: хосты первыми
    /// (StartProcess), затем последние существующие файлы (OpenFile).
    /// План не записывается в БД - запись делает ExecutePlanAsync.
    /// </summary>
    public async Task<RestorePlan> BuildPlanAsync(
        long? sessionId = null,
        IReadOnlyList<RestoreHostApp>? hosts = null,
        CancellationToken ct = default)
    {
        try
        {
            return await BuildPlanCoreAsync(sessionId, hosts, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Сбой построения плана восстановления - возвращён пустой план");
            return EmptyPlan();
        }
    }

    /// <summary>
    /// Выполняет план: сохраняет его в БД, идёт по шагам в порядке Seq.
    /// dryRun помечает все шаги Skipped, не запуская НИЧЕГО. Падение одного
    /// шага не отменяет остальные. Никогда не бросает.
    /// </summary>
    public async Task<RestorePlan> ExecutePlanAsync(RestorePlan plan, bool dryRun, CancellationToken ct = default)
    {
        if (plan is null)
        {
            _logger.LogWarning("ExecutePlanAsync получил null вместо плана");
            return EmptyPlan();
        }

        // Без снапшота или без шагов план не выполняется и не записывается:
        // план - восстановление снапшота, без него восстанавливать нечего
        if (plan.SnapshotId == 0 || plan.Steps.Count == 0)
        {
            return plan;
        }

        try
        {
            return await ExecutePlanCoreAsync(plan, dryRun, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Сбой выполнения плана восстановления - план возвращён без результатов");
            return plan;
        }
    }

    private async Task<RestorePlan> BuildPlanCoreAsync(
        long? sessionId,
        IReadOnlyList<RestoreHostApp>? hosts,
        CancellationToken ct)
    {
        long effectiveSessionId;
        if (sessionId is { } explicitId)
        {
            effectiveSessionId = explicitId;
        }
        else
        {
            var session = await _repository.GetLastSessionAsync(ct).ConfigureAwait(false);
            if (session is null)
            {
                return EmptyPlan(); // сессий ещё не было - восстанавливать нечего
            }

            effectiveSessionId = session.Id;
        }

        // Якорь снапшота: без него SnapshotId = 0 и план останется невыполнимым
        var snapshot = await _repository.GetLastSnapshotAsync(effectiveSessionId, ct).ConfigureAwait(false);
        var snapshotId = snapshot?.Id ?? 0;

        var steps = new List<RestoreStep>();
        var seq = 1;

        // Хосты первыми (хост → окно → файлы, readme): приложения открывают
        // «окна», в которые затем восстанавливаются файлы
        if (hosts is not null)
        {
            foreach (var host in hosts)
            {
                if (string.IsNullOrWhiteSpace(host.ProcessName))
                {
                    continue;
                }

                steps.Add(new RestoreStep(
                    Id: 0,
                    PlanId: 0,
                    Seq: seq++,
                    Strategy: RestoreStrategyKind.StartProcess,
                    Target: host.ProcessName.Trim(),
                    ArgsJson: host.Arguments is null
                        ? null
                        : JsonSerializer.Serialize(new { arguments = host.Arguments }),
                    Result: null,
                    Error: null,
                    DurationMs: null));
            }
        }

        // Файлы: distinct по пути, последние 10, существующие на диске;
        // порядок - от самого свежего к старому (важное восстанавливается первым)
        var activities = await _repository.GetSessionFileActivityAsync(effectiveSessionId, ct).ConfigureAwait(false);
        var recentFiles = activities
            .GroupBy(activity => activity.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Path = group.Key, LastTs = group.Max(activity => activity.Ts) })
            .OrderByDescending(item => item.LastTs)
            .Take(MaxFileSteps)
            .Where(item => File.Exists(item.Path))
            .ToList();

        foreach (var file in recentFiles)
        {
            steps.Add(new RestoreStep(
                Id: 0,
                PlanId: 0,
                Seq: seq++,
                Strategy: RestoreStrategyKind.OpenFile,
                Target: file.Path,
                ArgsJson: null,
                Result: null,
                Error: null,
                DurationMs: null));
        }

        return new RestorePlan(
            Id: 0,
            SnapshotId: snapshotId,
            CreatedAt: _clock.UtcNow,
            DryRun: false,
            Status: RestorePlanStatus.Pending,
            StartedAt: null,
            FinishedAt: null)
        {
            Steps = steps,
        };
    }

    private async Task<RestorePlan> ExecutePlanCoreAsync(RestorePlan plan, bool dryRun, CancellationToken ct)
    {
        // План пишется в БД ДО выполнения; DryRun в записи отражает режим этого запуска
        var startedAt = _clock.UtcNow;
        var persisted = plan with { DryRun = dryRun, StartedAt = startedAt };
        var planId = await _repository.CreateRestorePlanAsync(persisted, ct).ConfigureAwait(false);

        // Шагам присвоены Id в БД; перечитываем последний план снапшота -
        // это только что созданный план
        var stored = await _repository.GetLastRestorePlanAsync(plan.SnapshotId, ct).ConfigureAwait(false)
            ?? persisted with { Id = planId };

        var executedSteps = new List<RestoreStep>();
        foreach (var step in stored.Steps.OrderBy(s => s.Seq))
        {
            RestoreStep executed;
            if (dryRun)
            {
                executed = step with { Result = RestoreStepResult.Skipped, Error = "dry-run: не запускался", DurationMs = 0 };
            }
            else
            {
                executed = ExecuteStep(step);
            }

            executedSteps.Add(executed);

            // Результат каждого шага фиксируется сразу после его выполнения
            if (step.Id > 0)
            {
                try
                {
                    await _repository.SetRestoreStepResultAsync(
                        step.Id,
                        executed.Result ?? RestoreStepResult.Failed,
                        executed.Error,
                        executed.DurationMs,
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Не удалось записать результат шага {StepId}", step.Id);
                }
            }
        }

        // Итог: все успех → Success; есть сбой → Partial; все сбой → Failed
        var finishedAt = _clock.UtcNow;
        var finalStatus = ComputePlanStatus(executedSteps);
        try
        {
            await _repository.SetRestorePlanStatusAsync(planId, finalStatus, startedAt, finishedAt, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось записать статус плана {PlanId}", planId);
        }

        return new RestorePlan(
            Id: planId,
            SnapshotId: plan.SnapshotId,
            CreatedAt: stored.CreatedAt,
            DryRun: dryRun,
            Status: finalStatus,
            StartedAt: startedAt,
            FinishedAt: finishedAt)
        {
            Steps = executedSteps,
        };
    }

    private RestoreStep ExecuteStep(RestoreStep step)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var (result, error) = step.Strategy switch
            {
                RestoreStrategyKind.StartProcess => ExecuteStartProcess(step),
                RestoreStrategyKind.OpenFile => ExecuteOpenFile(step),
                // OpenUrl/ExecuteCommand/CustomAdapter появятся после MVP
                _ => (RestoreStepResult.Skipped, "Стратегия не поддерживается в MVP"),
            };
            stopwatch.Stop();
            return step with { Result = result, Error = error, DurationMs = stopwatch.ElapsedMilliseconds };
        }
        catch (Exception ex)
        {
            // Падение шага не отменяет остальные - фиксируем и идём дальше
            return step with { Result = RestoreStepResult.Failed, Error = ex.Message, DurationMs = null };
        }
    }

    private (RestoreStepResult Result, string? Error) ExecuteStartProcess(RestoreStep step)
    {
        var target = step.Target?.Trim();
        if (string.IsNullOrEmpty(target))
        {
            return (RestoreStepResult.Failed, "Не задано приложение");
        }

        // Идемпотентность: повторное восстановление не плодит дубликаты процессов.
        // Файл, открытый неизвестно кем, так не проверить - OpenFile выполняется честно
        if (_runningCheck.IsRunning(TrimExeSuffix(target)))
        {
            return (RestoreStepResult.Skipped, "уже запущен");
        }

        var executablePath = _resolver.ResolveExecutable(target);
        if (string.IsNullOrEmpty(executablePath))
        {
            return (RestoreStepResult.Failed, $"Приложение не найдено: {target}");
        }

        var arguments = ParseArguments(step.ArgsJson);
        return _launcher.TryStart(executablePath, arguments)
            ? (RestoreStepResult.Success, null)
            : (RestoreStepResult.Failed, $"Не удалось запустить: {executablePath}");
    }

    private (RestoreStepResult Result, string? Error) ExecuteOpenFile(RestoreStep step)
    {
        var target = step.Target;
        if (string.IsNullOrEmpty(target) || !File.Exists(target))
        {
            return (RestoreStepResult.Failed, "Файл не существует");
        }

        return _launcher.TryStart(target)
            ? (RestoreStepResult.Success, null)
            : (RestoreStepResult.Failed, $"Не удалось открыть: {target}");
    }

    private static RestorePlanStatus ComputePlanStatus(IReadOnlyList<RestoreStep> steps)
    {
        var results = steps.Select(step => step.Result).ToList();
        if (results.Count == 0)
        {
            return RestorePlanStatus.Success; // шагов нет - нечему было падать
        }

        var failed = results.Count(result => result == RestoreStepResult.Failed);
        if (failed == 0)
        {
            return RestorePlanStatus.Success;
        }

        return failed == results.Count ? RestorePlanStatus.Failed : RestorePlanStatus.Partial;
    }

    /// <summary>ArgsJson вида {"arguments":"..."} → аргументы; битый JSON → null.</summary>
    private static string? ParseArguments(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<HostArguments>(argsJson)?.arguments;
        }
        catch
        {
            return null; // битый JSON - запускаем без аргументов
        }
    }

    /// <summary>«code.exe» → «code».</summary>
    private static string TrimExeSuffix(string processName)
    {
        var baseName = processName.Trim();
        return baseName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? baseName[..^4]
            : baseName;
    }

    private RestorePlan EmptyPlan() => new(
        Id: 0,
        SnapshotId: 0,
        CreatedAt: _clock.UtcNow,
        DryRun: false,
        Status: RestorePlanStatus.Pending,
        StartedAt: null,
        FinishedAt: null);

    /// <summary>DTO разбора ArgsJson (имя поля - как в JSON: "arguments").</summary>
    private sealed class HostArguments
    {
        public string? arguments { get; set; }
    }
}
