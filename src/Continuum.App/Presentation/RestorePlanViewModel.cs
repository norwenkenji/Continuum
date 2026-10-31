using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Continuum.Core.Domain;
using Continuum.Restore;

namespace Continuum.Presentation;

/// <summary>Индикация результата шага для цветовой подсветки в UI.</summary>
public enum RestoreStepResultKind
{
    Pending,
    Success,
    Failed,
    Skipped
}

/// <summary>Строка списка шагов плана восстановления: данные для отображения.</summary>
public sealed class RestoreStepViewModel
{
    public int Seq { get; init; }

    /// <summary>Стратегия по-русски («Запуск приложения», «Открытие файла» и т.п.).</summary>
    public string StrategyText { get; init; } = string.Empty;

    public string? Target { get; init; }

    /// <summary>Результат по-русски: «Успех», «Сбой», «Пропущен», «Ожидает».</summary>
    public string ResultText { get; init; } = string.Empty;

    public string? Error { get; init; }

    public RestoreStepResultKind ResultKind { get; init; }
}

/// <summary>
/// Модель представления плана восстановления (MVVM): построение плана,
/// dry-run и настоящее выполнение через RestoreService. Команды async void
/// с try/catch - UI не падает; await без ConfigureAwait(false), чтобы
/// обновления коллекции возвращались на поток UI (в тестах контекста нет,
/// и это тоже безопасно).
/// </summary>
public sealed class RestorePlanViewModel : ViewModelBase
{
    private readonly RestoreService _restoreService;
    private RestorePlan? _lastPlan;
    private bool _isBusy;
    private string _planSummary = "План ещё не построен";

    public RestorePlanViewModel(RestoreService restoreService)
    {
        _restoreService = restoreService ?? throw new ArgumentNullException(nameof(restoreService));
        BuildCommand = new RelayCommand(async () => await BuildPlanAsync(), () => !IsBusy);
        DryRunCommand = new RelayCommand(async () => await RunPlanAsync(dryRun: true), () => !IsBusy);
        ExecuteCommand = new RelayCommand(async () => await RunPlanAsync(dryRun: false), () => !IsBusy);
    }

    /// <summary>Идёт операция (построение или выполнение) - команды заблокированы.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    /// <summary>Сводка плана: «Шагов: N, успех: X, сбои: Y».</summary>
    public string PlanSummary
    {
        get => _planSummary;
        private set => SetField(ref _planSummary, value);
    }

    public ObservableCollection<RestoreStepViewModel> Steps { get; } = [];

    public RelayCommand BuildCommand { get; }

    public RelayCommand DryRunCommand { get; }

    public RelayCommand ExecuteCommand { get; }

    /// <summary>Собирает план из последней сессии и показывает шаги.</summary>
    private async Task BuildPlanAsync()
    {
        IsBusy = true;
        try
        {
            _lastPlan = await _restoreService.BuildPlanAsync();
            RenderPlan(_lastPlan);
        }
        catch
        {
            // UI не падает: сбой построения оставляет прежнее состояние
            _lastPlan = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Выполняет последний построенный план (или строит его на месте).</summary>
    private async Task RunPlanAsync(bool dryRun)
    {
        IsBusy = true;
        try
        {
            var plan = _lastPlan ?? await _restoreService.BuildPlanAsync();
            _lastPlan = await _restoreService.ExecutePlanAsync(plan, dryRun);
            RenderPlan(_lastPlan);
        }
        catch
        {
            // UI не падает: сбой выполнения оставляет прежнее состояние
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RenderPlan(RestorePlan plan)
    {
        Steps.Clear();
        foreach (var step in plan.Steps.OrderBy(step => step.Seq))
        {
            Steps.Add(MapStep(step));
        }

        var success = plan.Steps.Count(step => step.Result == RestoreStepResult.Success);
        var failed = plan.Steps.Count(step => step.Result == RestoreStepResult.Failed);
        PlanSummary = $"Шагов: {plan.Steps.Count}, успех: {success}, сбои: {failed}";
    }

    private static RestoreStepViewModel MapStep(RestoreStep step) => new()
    {
        Seq = step.Seq,
        StrategyText = MapStrategy(step.Strategy),
        Target = step.Target,
        ResultText = MapResult(step.Result),
        Error = step.Error,
        ResultKind = MapResultKind(step.Result),
    };

    private static string MapStrategy(RestoreStrategyKind strategy) => strategy switch
    {
        RestoreStrategyKind.StartProcess => "Запуск приложения",
        RestoreStrategyKind.OpenFile => "Открытие файла",
        RestoreStrategyKind.OpenUrl => "Открытие URL",
        RestoreStrategyKind.ExecuteCommand => "Команда",
        _ => "Адаптер",
    };

    private static string MapResult(RestoreStepResult? result) => result switch
    {
        RestoreStepResult.Success => "Успех",
        RestoreStepResult.Failed => "Сбой",
        RestoreStepResult.Skipped => "Пропущен",
        _ => "Ожидает",
    };

    private static RestoreStepResultKind MapResultKind(RestoreStepResult? result) => result switch
    {
        RestoreStepResult.Success => RestoreStepResultKind.Success,
        RestoreStepResult.Failed => RestoreStepResultKind.Failed,
        RestoreStepResult.Skipped => RestoreStepResultKind.Skipped,
        _ => RestoreStepResultKind.Pending,
    };
}
