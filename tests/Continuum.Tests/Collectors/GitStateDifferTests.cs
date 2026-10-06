using System.Collections.Generic;
using System.Linq;
using Continuum.Collectors;
using Continuum.Core.Domain;
using Xunit;

namespace Continuum.Tests.Collectors;

/// <summary>
/// Дифф dirty-набора git: события только на появление/смену вида,
/// исчезновение молчит, первый снапшот - база.
/// </summary>
public class GitStateDifferTests
{
    private static Dictionary<string, FileChangeKind> Snapshot(params (string Path, FileChangeKind Kind)[] files) =>
        files.ToDictionary(f => f.Path, f => f.Kind);

    [Fact]
    public void First_snapshot_is_baseline_no_changes()
    {
        var differ = new GitStateDiffer();

        var changes = differ.Process(Snapshot(("a.txt", FileChangeKind.Modified), ("b.txt", FileChangeKind.Untracked)));

        Assert.Empty(changes);
    }

    [Fact]
    public void New_dirty_file_gives_change()
    {
        var differ = new GitStateDiffer();
        differ.Process(Snapshot(("a.txt", FileChangeKind.Modified)));

        var changes = differ.Process(Snapshot(("a.txt", FileChangeKind.Modified), ("b.txt", FileChangeKind.Untracked)));

        var change = Assert.Single(changes);
        Assert.Equal("b.txt", change.Path);
        Assert.Equal(FileChangeKind.Untracked, change.Kind);
    }

    [Fact]
    public void Kind_change_gives_change()
    {
        var differ = new GitStateDiffer();
        differ.Process(Snapshot(("a.txt", FileChangeKind.Modified)));

        var changes = differ.Process(Snapshot(("a.txt", FileChangeKind.Deleted)));

        var change = Assert.Single(changes);
        Assert.Equal("a.txt", change.Path);
        Assert.Equal(FileChangeKind.Deleted, change.Kind);
    }

    [Fact]
    public void Disappearing_from_dirty_is_silent()
    {
        var differ = new GitStateDiffer();
        differ.Process(Snapshot(("a.txt", FileChangeKind.Modified), ("b.txt", FileChangeKind.Untracked)));

        // b.txt закоммичен или откачен - это не событие
        var changes = differ.Process(Snapshot(("a.txt", FileChangeKind.Modified)));

        Assert.Empty(changes);
    }

    [Fact]
    public void Unchanged_paths_do_not_interfere()
    {
        var differ = new GitStateDiffer();
        differ.Process(Snapshot(("a.txt", FileChangeKind.Modified), ("b.txt", FileChangeKind.Untracked)));

        var changes = differ.Process(Snapshot(
            ("a.txt", FileChangeKind.Modified),
            ("b.txt", FileChangeKind.Untracked),
            ("c.txt", FileChangeKind.Added)));

        var change = Assert.Single(changes);
        Assert.Equal("c.txt", change.Path);
        Assert.Equal(FileChangeKind.Added, change.Kind);
    }

    [Fact]
    public void Reset_makes_next_snapshot_baseline_again()
    {
        var differ = new GitStateDiffer();
        differ.Process(Snapshot(("a.txt", FileChangeKind.Modified)));

        differ.Reset();
        var changes = differ.Process(Snapshot(("a.txt", FileChangeKind.Modified), ("b.txt", FileChangeKind.Untracked)));

        Assert.Empty(changes);
    }
}
