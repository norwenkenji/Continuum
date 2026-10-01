using System.Collections.Generic;
using System.Linq;
using Continuum.Collectors;
using Xunit;

namespace Continuum.Tests.Collectors;

/// <summary>
/// Дифф снапшотов процессов: started/exited эмитятся только на изменения,
/// первый снапшот - базовая точка отсчёта, а не события.
/// </summary>
public class ProcessListDifferTests
{
    private static Dictionary<int, string> Snapshot(params (int Pid, string Name)[] processes) =>
        processes.ToDictionary(p => p.Pid, p => p.Name);

    [Fact]
    public void First_snapshot_is_baseline_no_events()
    {
        var differ = new ProcessListDiffer();

        var diff = differ.Process(Snapshot((1, "a.exe"), (2, "b.exe")));

        // Иначе старт монитора эмитил бы app_started для всех процессов системы
        Assert.Empty(diff.Started);
        Assert.Empty(diff.Exited);
    }

    [Fact]
    public void New_pids_are_started()
    {
        var differ = new ProcessListDiffer();
        differ.Process(Snapshot((1, "a.exe")));

        var diff = differ.Process(Snapshot((1, "a.exe"), (2, "b.exe"), (3, "c.exe")));

        Assert.Empty(diff.Exited);
        Assert.Equal(2, diff.Started.Count);
        Assert.Contains(diff.Started, p => p.ProcessId == 2 && p.Name == "b.exe");
        Assert.Contains(diff.Started, p => p.ProcessId == 3 && p.Name == "c.exe");
    }

    [Fact]
    public void Removed_pids_are_exited()
    {
        var differ = new ProcessListDiffer();
        differ.Process(Snapshot((1, "a.exe"), (2, "b.exe")));

        var diff = differ.Process(Snapshot((1, "a.exe")));

        Assert.Empty(diff.Started);
        var exited = Assert.Single(diff.Exited);
        Assert.Equal(2, exited.ProcessId);
        Assert.Equal("b.exe", exited.Name);
    }

    [Fact]
    public void No_changes_no_events()
    {
        var differ = new ProcessListDiffer();
        differ.Process(Snapshot((1, "a.exe"), (2, "b.exe")));

        var diff = differ.Process(Snapshot((1, "a.exe"), (2, "b.exe")));

        Assert.Empty(diff.Started);
        Assert.Empty(diff.Exited);
    }

    [Fact]
    public void Reused_pid_with_other_name_is_exit_and_start()
    {
        var differ = new ProcessListDiffer();
        differ.Process(Snapshot((1, "a.exe")));

        var diff = differ.Process(Snapshot((1, "b.exe")));

        // PID переиспользован: старый процесс вышел, новый стартовал
        var exited = Assert.Single(diff.Exited);
        Assert.Equal("a.exe", exited.Name);
        var started = Assert.Single(diff.Started);
        Assert.Equal("b.exe", started.Name);
    }

    [Fact]
    public void Reset_makes_next_snapshot_baseline_again()
    {
        var differ = new ProcessListDiffer();
        differ.Process(Snapshot((1, "a.exe")));

        differ.Reset();
        var diff = differ.Process(Snapshot((1, "a.exe"), (2, "b.exe")));

        Assert.Empty(diff.Started);
        Assert.Empty(diff.Exited);
    }
}
