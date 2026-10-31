using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Restore;
using Continuum.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Continuum.Tests.Restore;

/// <summary>
/// RestoreService на FakeRepository и стабах резолвера/лаунчера/часов:
/// построение плана, dry-run, выполнение, идемпотентность, статусы.
/// НИЧЕГО реального не запускается - IProcessLauncher подменён.
/// </summary>
public class RestoreServiceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "continuum-restore-tests", Guid.NewGuid().ToString("N"));

    public RestoreServiceTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task BuildPlan_without_sessions_gives_empty_pending_plan()
    {
        var service = CreateService(new FakeRepository());

        var plan = await service.BuildPlanAsync();

        Assert.Equal(0, plan.Id);
        Assert.Equal(0, plan.SnapshotId);
        Assert.Empty(plan.Steps);
        Assert.Equal(RestorePlanStatus.Pending, plan.Status);
        Assert.False(plan.DryRun);
        Assert.Null(plan.StartedAt);
    }

    [Fact]
    public async Task BuildPlan_takes_last_ten_distinct_existing_files()
    {
        var repository = new FakeRepository();
        SeedSessionWithSnapshot(repository);
        var service = CreateService(repository);

        // 12 существующих + повтор старого (должен схлопнуться) + несуществующий
        var files = new List<string>();
        for (var i = 1; i <= 12; i++)
        {
            files.Add(CreateFile($"f{i}.txt"));
            repository.FileActivities.Add(MakeActivity(1, files[^1], minute: i));
        }

        repository.FileActivities.Add(MakeActivity(1, files[0], minute: 20)); // повтор f1 - тот же путь
        repository.FileActivities.Add(MakeActivity(1, Path.Combine(_tempRoot, "ghost.txt"), minute: 15));

        var plan = await service.BuildPlanAsync();

        Assert.All(plan.Steps, step => Assert.Equal(RestoreStrategyKind.OpenFile, step.Strategy));
        Assert.Equal(9, plan.Steps.Count); // 13 путей distinct → 10 свежих → ghost отсеян по существованию

        // Порядок: от самого свежего к старому; Seq сквозной с 1
        Assert.Equal(files[0], plan.Steps[0].Target);
        Assert.Equal(1, plan.Steps[0].Seq);
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            Assert.Equal(i + 1, plan.Steps[i].Seq);
        }

        // Отсечённые: несуществующий и 4 самых старых (f2-f4 вне топ-10, ghost выпал)
        Assert.DoesNotContain(plan.Steps, s => s.Target!.EndsWith("ghost.txt"));
        Assert.DoesNotContain(plan.Steps, s => s.Target!.EndsWith("f4.txt"));
        Assert.DoesNotContain(plan.Steps, s => s.Target!.EndsWith("f3.txt"));
        Assert.DoesNotContain(plan.Steps, s => s.Target!.EndsWith("f2.txt"));
    }

    [Fact]
    public async Task BuildPlan_hosts_go_first_with_arguments_json()
    {
        var repository = new FakeRepository();
        SeedSessionWithSnapshot(repository);
        var file = CreateFile("readme.md");
        repository.FileActivities.Add(MakeActivity(1, file, minute: 1));
        var service = CreateService(repository);

        var plan = await service.BuildPlanAsync(
            hosts: [new RestoreHostApp("code", "-g readme.md"), new RestoreHostApp("wt", null)]);

        Assert.Equal(3, plan.Steps.Count);
        Assert.Equal(RestoreStrategyKind.StartProcess, plan.Steps[0].Strategy);
        Assert.Equal("code", plan.Steps[0].Target);
        Assert.Equal("{\"arguments\":\"-g readme.md\"}", plan.Steps[0].ArgsJson);
        Assert.Equal(1, plan.Steps[0].Seq);

        Assert.Equal(RestoreStrategyKind.StartProcess, plan.Steps[1].Strategy);
        Assert.Equal("wt", plan.Steps[1].Target);
        Assert.Null(plan.Steps[1].ArgsJson); // без аргументов - без ArgsJson

        Assert.Equal(RestoreStrategyKind.OpenFile, plan.Steps[2].Strategy);
        Assert.Equal(file, plan.Steps[2].Target);
        Assert.Null(plan.Steps[2].ArgsJson);
        Assert.Equal(7, plan.SnapshotId);
    }

    [Fact]
    public async Task BuildPlan_without_snapshot_keeps_zero_snapshot_id()
    {
        var repository = new FakeRepository();
        repository.Sessions.Add(MakeSession(1)); // сессия есть, снапшота нет
        var file = CreateFile("a.txt");
        repository.FileActivities.Add(MakeActivity(1, file, minute: 1));
        var service = CreateService(repository);

        var plan = await service.BuildPlanAsync();

        // План строится, но без якоря снапшота выполняться не будет
        Assert.Equal(0, plan.SnapshotId);
        Assert.NotEmpty(plan.Steps);
        Assert.Equal(RestorePlanStatus.Pending, plan.Status);
    }

    [Fact]
    public async Task ExecutePlan_without_snapshot_returns_plan_without_execution_and_db_write()
    {
        var repository = new FakeRepository();
        repository.Sessions.Add(MakeSession(1));
        var file = CreateFile("a.txt");
        repository.FileActivities.Add(MakeActivity(1, file, minute: 1));
        var (service, launcher) = CreateServiceWithLauncher(repository);

        var plan = await service.BuildPlanAsync();
        var result = await service.ExecutePlanAsync(plan, dryRun: false);

        Assert.Equal(plan, result); // план как есть
        Assert.Empty(repository.RestorePlans);
        Assert.Empty(launcher.Calls);
    }

    [Fact]
    public async Task ExecutePlan_with_empty_steps_returns_plan_without_db_write()
    {
        var repository = new FakeRepository();
        SeedSessionWithSnapshot(repository);
        var (service, launcher) = CreateServiceWithLauncher(repository);

        var plan = await service.BuildPlanAsync(); // снапшот есть, шагов нет
        var result = await service.ExecutePlanAsync(plan, dryRun: false);

        Assert.Equal(plan, result);
        Assert.Empty(repository.RestorePlans);
        Assert.Empty(launcher.Calls);
    }

    [Fact]
    public async Task ExecutePlan_dry_run_starts_nothing_and_skips_all_steps()
    {
        var repository = new FakeRepository();
        SeedSessionWithSnapshot(repository);
        var file = CreateFile("a.txt");
        repository.FileActivities.Add(MakeActivity(1, file, minute: 1));
        var resolver = new StubResolver();
        var (service, launcher) = CreateServiceWithLauncher(repository, resolver);

        var plan = await service.BuildPlanAsync(hosts: [new RestoreHostApp("code", null)]);
        var result = await service.ExecutePlanAsync(plan, dryRun: true);

        // НИ ОДНОГО запуска и ни одного резолва
        Assert.Empty(launcher.Calls);
        Assert.Equal(0, resolver.Calls);

        Assert.True(result.DryRun);
        Assert.Equal(RestorePlanStatus.Success, result.Status);
        Assert.All(result.Steps, step =>
        {
            Assert.Equal(RestoreStepResult.Skipped, step.Result);
            Assert.Equal("dry-run: не запускался", step.Error);
        });
        Assert.NotNull(result.StartedAt);
        Assert.NotNull(result.FinishedAt);

        // Результаты записаны в БД: план с шагами, получившими Id
        var stored = Assert.Single(repository.RestorePlans);
        Assert.Equal(result.Id, stored.Id);
        Assert.True(stored.DryRun);
        Assert.All(stored.Steps, step =>
        {
            Assert.True(step.Id > 0);
            Assert.Equal(RestoreStepResult.Skipped, step.Result); // SetRestoreStepResultAsync применился
        });
    }

    [Fact]
    public async Task ExecutePlan_file_step_succeeds_and_is_persisted()
    {
        var repository = new FakeRepository();
        SeedSessionWithSnapshot(repository);
        var file = CreateFile("a.txt");
        repository.FileActivities.Add(MakeActivity(1, file, minute: 1));
        var (service, launcher) = CreateServiceWithLauncher(repository);

        var plan = await service.BuildPlanAsync();
        var result = await service.ExecutePlanAsync(plan, dryRun: false);

        var step = Assert.Single(result.Steps);
        Assert.Equal(RestoreStepResult.Success, step.Result);
        Assert.Null(step.Error);
        Assert.NotNull(step.DurationMs);
        Assert.True(step.Id > 0);
        Assert.Equal(RestorePlanStatus.Success, result.Status);

        // Запуск ровно один, файлом, без аргументов
        var call = Assert.Single(launcher.Calls);
        Assert.Equal(file, call.FileName);
        Assert.Null(call.Arguments);

        // Идемпотентность плана в БД: один план, результат шага записан
        var stored = Assert.Single(repository.RestorePlans);
        Assert.Equal(RestoreStepResult.Success, stored.Steps[0].Result);
    }

    [Fact]
    public async Task ExecutePlan_missing_file_fails()
    {
        var repository = new FakeRepository();
        SeedSessionWithSnapshot(repository);
        var file = CreateFile("a.txt");
        repository.FileActivities.Add(MakeActivity(1, file, minute: 1));
        var (service, launcher) = CreateServiceWithLauncher(repository);

        var plan = await service.BuildPlanAsync();
        File.Delete(file); // файл исчез между построением и выполнением

        var result = await service.ExecutePlanAsync(plan, dryRun: false);

        var step = Assert.Single(result.Steps);
        Assert.Equal(RestoreStepResult.Failed, step.Result);
        Assert.Equal("Файл не существует", step.Error);
        Assert.Empty(launcher.Calls);
        Assert.Equal(RestorePlanStatus.Failed, result.Status);
    }

    [Fact]
    public async Task ExecutePlan_unknown_application_fails()
    {
        var repository = new FakeRepository();
        SeedSessionWithSnapshot(repository);
        var resolver = new StubResolver { Resolve = _ => null };
        var (service, launcher) = CreateServiceWithLauncher(repository, resolver);

        var plan = await service.BuildPlanAsync(hosts: [new RestoreHostApp("ghost", null)]);
        var result = await service.ExecutePlanAsync(plan, dryRun: false);

        var step = Assert.Single(result.Steps);
        Assert.Equal(RestoreStepResult.Failed, step.Result);
        Assert.Equal("Приложение не найдено: ghost", step.Error);
        Assert.Empty(launcher.Calls);
    }

    [Fact]
    public async Task ExecutePlan_launcher_failure_fails_with_arguments_passed()
    {
        var repository = new FakeRepository();
        SeedSessionWithSnapshot(repository);
        var resolver = new StubResolver { Resolve = name => $"C:\\fake\\{name}.exe" };
        var (service, launcher) = CreateServiceWithLauncher(repository, resolver);
        launcher.ShouldStart = _ => false;

        var plan = await service.BuildPlanAsync(hosts: [new RestoreHostApp("code", "-g readme.md")]);
        var result = await service.ExecutePlanAsync(plan, dryRun: false);

        var step = Assert.Single(result.Steps);
        Assert.Equal(RestoreStepResult.Failed, step.Result);
        Assert.Equal("Не удалось запустить: C:\\fake\\code.exe", step.Error);

        // Лаунчер получил РЕЗОЛВНУТЫЙ путь и аргументы из ArgsJson
        var call = Assert.Single(launcher.Calls);
        Assert.Equal("C:\\fake\\code.exe", call.FileName);
        Assert.Equal("-g readme.md", call.Arguments);
    }

    [Fact]
    public async Task ExecutePlan_one_failure_does_not_cancel_the_rest()
    {
        var repository = new FakeRepository();
        SeedSessionWithSnapshot(repository);
        var file = CreateFile("a.txt");
        repository.FileActivities.Add(MakeActivity(1, file, minute: 1));
        var resolver = new StubResolver { Resolve = name => name == "ghost" ? null : $"C:\\fake\\{name}.exe" };
        var (service, launcher) = CreateServiceWithLauncher(repository, resolver);

        // Первый шаг падает (приложение не найдено), второй обязан выполниться
        var plan = await service.BuildPlanAsync(hosts: [new RestoreHostApp("ghost", null)]);
        var result = await service.ExecutePlanAsync(plan, dryRun: false);

        Assert.Equal(2, result.Steps.Count);
        Assert.Equal(RestoreStepResult.Failed, result.Steps[0].Result);
        Assert.Equal(RestoreStepResult.Success, result.Steps[1].Result);
        Assert.Single(launcher.Calls); // файловый шаг запущен после падения первого
        Assert.Equal(RestorePlanStatus.Partial, result.Status);
    }

    [Fact]
    public async Task ExecutePlan_already_running_process_is_skipped()
    {
        var repository = new FakeRepository();
        SeedSessionWithSnapshot(repository);
        var resolver = new StubResolver { Resolve = name => $"C:\\fake\\{name}.exe" };
        var runningCheck = new StubRunningProcessCheck();
        runningCheck.Running.Add("code");
        var (service, launcher) = CreateServiceWithLauncher(repository, resolver, runningCheck);

        var plan = await service.BuildPlanAsync(hosts: [new RestoreHostApp("code", null)]);
        var result = await service.ExecutePlanAsync(plan, dryRun: false);

        var step = Assert.Single(result.Steps);
        Assert.Equal(RestoreStepResult.Skipped, step.Result);
        Assert.Equal("уже запущен", step.Error);
        Assert.Empty(launcher.Calls); // повтор не плодит дубликаты процессов
        Assert.Equal(RestorePlanStatus.Success, result.Status);
    }

    private RestoreService CreateService(FakeRepository repository) =>
        CreateServiceWithLauncher(repository).Service;

    private (RestoreService Service, StubLauncher Launcher) CreateServiceWithLauncher(
        FakeRepository repository,
        StubResolver? resolver = null,
        StubRunningProcessCheck? runningCheck = null)
    {
        var launcher = new StubLauncher();
        var service = new RestoreService(
            repository,
            resolver ?? new StubResolver(),
            launcher,
            new StubClock(),
            NullLogger<RestoreService>.Instance,
            runningCheck ?? new StubRunningProcessCheck());
        return (service, launcher);
    }

    private void SeedSessionWithSnapshot(FakeRepository repository)
    {
        repository.Sessions.Add(MakeSession(1));
        repository.Snapshots.Add(new Snapshot(7, 1, T0.AddMinutes(30), SnapshotReason.SessionEnd, 1, "{}"));
    }

    private Session MakeSession(long id) =>
        new(id, T0, T0.AddHours(1), SessionStatus.Ended, SessionEndReason.User, IdleThresholdSeconds: 900);

    private FileActivity MakeActivity(long sessionId, string path, int minute) =>
        new(0, sessionId, T0.AddMinutes(minute), ProjectId: null, path, FileChangeKind.Modified, FileActivitySource.Watcher);

    private string CreateFile(string name)
    {
        var path = Path.Combine(_tempRoot, name);
        File.WriteAllText(path, "stub");
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    /// <summary>Стаб резолвера: карта имя → путь; null - не найден.</summary>
    private sealed class StubResolver : IApplicationResolver
    {
        public Func<string, string?> Resolve { get; set; } = _ => null;

        public int Calls { get; private set; }

        public string? ResolveExecutable(string applicationName)
        {
            Calls++;
            return Resolve(applicationName);
        }
    }

    /// <summary>Стаб лаунчера: считает запуски, успех настраивается.</summary>
    private sealed class StubLauncher : IProcessLauncher
    {
        public List<(string FileName, string? Arguments, string? WorkingDirectory)> Calls { get; } = [];

        public Func<string, bool> ShouldStart { get; set; } = _ => true;

        public bool TryStart(string fileName, string? arguments = null, string? workingDirectory = null)
        {
            Calls.Add((fileName, arguments, workingDirectory));
            return ShouldStart(fileName);
        }
    }

    /// <summary>Стаб проверки «уже запущен».</summary>
    private sealed class StubRunningProcessCheck : IRunningProcessCheck
    {
        public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool IsRunning(string processName) => Running.Contains(processName);
    }

    /// <summary>Фиксированные часы.</summary>
    private sealed class StubClock : IClock
    {
        public DateTimeOffset UtcNow => T0;
    }
}
