using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Presentation;
using Continuum.Restore;
using Continuum.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Continuum.Tests.Presentation;

/// <summary>
/// RestorePlanViewModel на тех же стабах, что и RestoreService: команды
/// заполняют шаги, dry-run помечает «Пропущен», IsBusy гаснет после
/// операции, маппинг результат → текст.
/// </summary>
public class RestorePlanViewModelTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "continuum-restorevm-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeRepository _repository = new();
    private readonly StubLauncher _launcher = new();

    private readonly RestorePlanViewModel _viewModel;

    public RestorePlanViewModelTests()
    {
        Directory.CreateDirectory(_tempRoot);
        _repository.Sessions.Add(new Session(1, T0, T0.AddHours(1), SessionStatus.Ended, SessionEndReason.User, 900));
        _repository.Snapshots.Add(new Snapshot(7, 1, T0.AddMinutes(30), SnapshotReason.SessionEnd, 1, "{}"));

        var service = new RestoreService(
            _repository,
            new StubResolver(),
            _launcher,
            new StubClock(),
            NullLogger<RestoreService>.Instance,
            new StubRunningProcessCheck());
        _viewModel = new RestorePlanViewModel(service);
    }

    [Fact]
    public void BuildCommand_fills_steps_with_pending_state()
    {
        var file = CreateFileAndActivity("a.txt");

        _viewModel.BuildCommand.Execute(null);

        Assert.True(WaitFor(() => _viewModel.Steps.Count == 1));
        var step = _viewModel.Steps[0];
        Assert.Equal(1, step.Seq);
        Assert.Equal("Открытие файла", step.StrategyText);
        Assert.Equal(file, step.Target);
        Assert.Equal("Ожидает", step.ResultText);
        Assert.Equal(RestoreStepResultKind.Pending, step.ResultKind);
        Assert.Equal("Шагов: 1, успех: 0, сбои: 0", _viewModel.PlanSummary);
        Assert.False(_viewModel.IsBusy); // после завершения операции
    }

    [Fact]
    public void DryRunCommand_marks_steps_as_skipped_without_launching()
    {
        CreateFileAndActivity("a.txt");

        _viewModel.BuildCommand.Execute(null);
        Assert.True(WaitFor(() => _viewModel.Steps.Count == 1));

        _viewModel.DryRunCommand.Execute(null);
        Assert.True(WaitFor(() => _viewModel.Steps[0].ResultText == "Пропущен"));

        Assert.Equal(RestoreStepResultKind.Skipped, _viewModel.Steps[0].ResultKind);
        Assert.Equal("dry-run: не запускался", _viewModel.Steps[0].Error);
        Assert.Equal(0, _launcher.Calls); // ничего не запускалось
        Assert.Equal("Шагов: 1, успех: 0, сбои: 0", _viewModel.PlanSummary);
        Assert.False(_viewModel.IsBusy);
    }

    [Fact]
    public void ExecuteCommand_shows_success_text()
    {
        CreateFileAndActivity("a.txt");

        _viewModel.BuildCommand.Execute(null);
        Assert.True(WaitFor(() => _viewModel.Steps.Count == 1));

        _viewModel.ExecuteCommand.Execute(null);
        Assert.True(WaitFor(() => _viewModel.Steps[0].ResultText == "Успех"));

        Assert.Equal(RestoreStepResultKind.Success, _viewModel.Steps[0].ResultKind);
        Assert.Null(_viewModel.Steps[0].Error);
        Assert.Equal(1, _launcher.Calls);
        Assert.Equal("Шагов: 1, успех: 1, сбои: 0", _viewModel.PlanSummary);
        Assert.False(_viewModel.IsBusy);
    }

    [Fact]
    public void ExecuteCommand_failure_shows_failure_text()
    {
        var file = CreateFileAndActivity("a.txt");

        _viewModel.BuildCommand.Execute(null);
        Assert.True(WaitFor(() => _viewModel.Steps.Count == 1));

        File.Delete(file); // файл исчез между построением и выполнением

        _viewModel.ExecuteCommand.Execute(null);
        Assert.True(WaitFor(() => _viewModel.Steps[0].ResultText == "Сбой"));

        Assert.Equal(RestoreStepResultKind.Failed, _viewModel.Steps[0].ResultKind);
        Assert.Equal("Файл не существует", _viewModel.Steps[0].Error);
        Assert.Equal("Шагов: 1, успех: 0, сбои: 1", _viewModel.PlanSummary);
        Assert.False(_viewModel.IsBusy);
    }

    [Fact]
    public void BuildCommand_without_data_keeps_empty_state_without_crash()
    {
        // Сессия без файловой активности - план пуст, UI не падает
        _viewModel.BuildCommand.Execute(null);

        Assert.True(WaitFor(() => !_viewModel.IsBusy));
        Assert.Empty(_viewModel.Steps);
        Assert.StartsWith("Шагов: 0", _viewModel.PlanSummary);
    }

    private string CreateFileAndActivity(string name)
    {
        var path = Path.Combine(_tempRoot, name);
        File.WriteAllText(path, "stub");
        _repository.FileActivities.Add(new FileActivity(
            0, 1, T0.AddMinutes(1), ProjectId: null, path, FileChangeKind.Modified, FileActivitySource.Watcher));
        return path;
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(25);
        }

        return condition();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    /// <summary>Стаб резолвера: всегда «не найден» (файловым шагам не нужен).</summary>
    private sealed class StubResolver : IApplicationResolver
    {
        public string? ResolveExecutable(string applicationName) => null;
    }

    /// <summary>Стаб лаунчера: считает запуски, всегда успех.</summary>
    private sealed class StubLauncher : IProcessLauncher
    {
        public int Calls { get; private set; }

        public bool TryStart(string fileName, string? arguments = null, string? workingDirectory = null)
        {
            Calls++;
            return true;
        }
    }

    /// <summary>Стаб проверки «уже запущен»: ничего не запущено.</summary>
    private sealed class StubRunningProcessCheck : IRunningProcessCheck
    {
        public bool IsRunning(string processName) => false;
    }

    /// <summary>Фиксированные часы.</summary>
    private sealed class StubClock : IClock
    {
        public DateTimeOffset UtcNow => T0;
    }
}
