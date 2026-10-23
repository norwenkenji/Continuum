using Continuum.Collectors;
using Continuum.Core.Domain;
using Xunit;

namespace Continuum.Tests.Collectors;

/// <summary>
/// Дифф HEAD/ветки: базовый тик молчит, коммит, переключение ветки,
/// detached-переходы без событий.
/// </summary>
public class GitHeadDifferTests
{
    private const string Commit1 = "1111111111111111111111111111111111111111";
    private const string Commit2 = "2222222222222222222222222222222222222222";
    private const string Commit3 = "3333333333333333333333333333333333333333";

    [Fact]
    public void First_tick_is_baseline_no_event()
    {
        var differ = new GitHeadDiffer();

        var change = differ.Process("main", Commit1, "initial");

        Assert.Null(change);
    }

    [Fact]
    public void Same_state_is_silent()
    {
        var differ = new GitHeadDiffer();
        differ.Process("main", Commit1, "initial");

        Assert.Null(differ.Process("main", Commit1, "initial"));
    }

    [Fact]
    public void New_commit_on_same_branch_gives_git_commit_with_short_hash()
    {
        var differ = new GitHeadDiffer();
        differ.Process("main", Commit1, "initial");

        var change = differ.Process("main", Commit2, "fix: правка");

        Assert.NotNull(change);
        Assert.Equal(EventKind.GitCommit, change!.Kind);
        Assert.Equal("2222222 fix: правка", change.Value);
    }

    [Fact]
    public void Branch_switch_gives_branch_switch_event()
    {
        var differ = new GitHeadDiffer();
        differ.Process("main", Commit1, "initial");

        var change = differ.Process("feature", Commit1, "initial");

        Assert.NotNull(change);
        Assert.Equal(EventKind.GitBranchSwitch, change!.Kind);
        Assert.Equal("main -> feature", change.Value);
    }

    [Fact]
    public void Branch_and_commit_change_together_give_only_branch_switch()
    {
        var differ = new GitHeadDiffer();
        differ.Process("main", Commit1, "initial");

        var change = differ.Process("feature", Commit2, "other head");

        Assert.NotNull(change);
        Assert.Equal(EventKind.GitBranchSwitch, change!.Kind);
        Assert.Equal("main -> feature", change.Value);
    }

    [Fact]
    public void Entering_detached_head_is_silent_but_remembered()
    {
        var differ = new GitHeadDiffer();
        differ.Process("main", Commit1, "initial");

        // Уход в detached (ветка стала null) - не событие
        Assert.Null(differ.Process(null, Commit1, "initial"));

        // Новый коммит в detached при том же хэше-ветке: хэш сменился - это GitCommit
        var change = differ.Process(null, Commit2, "detached work");
        Assert.NotNull(change);
        Assert.Equal(EventKind.GitCommit, change!.Kind);
    }

    [Fact]
    public void Leaving_detached_head_is_silent()
    {
        var differ = new GitHeadDiffer();
        differ.Process("main", Commit1, "initial");
        differ.Process(null, Commit2, "detached"); // уход в detached

        // Выход из detached (ветка появилась, прежняя была null) - не эмитим:
        // назвать «с чего» переключились нельзя
        Assert.Null(differ.Process("main", Commit3, "back"));
    }

    [Fact]
    public void Reset_makes_next_tick_baseline_again()
    {
        var differ = new GitHeadDiffer();
        differ.Process("main", Commit1, "initial");

        differ.Reset();

        Assert.Null(differ.Process("feature", Commit2, "other"));
    }
}
