using Continuum.Collectors;
using Xunit;

namespace Continuum.Tests.Collectors;

/// <summary>
/// Критерий готовности шага 2: смена активного окна даёт РОВНО ОДНО событие.
/// </summary>
public class FocusTrackerTests
{
    [Fact]
    public void Changed_pair_pid_title_emits_exactly_once()
    {
        var tracker = new FocusTracker();

        Assert.True(tracker.ShouldEmit(100, "Editor"));
        Assert.False(tracker.ShouldEmit(100, "Editor")); // повтор того же состояния - тишина
        Assert.False(tracker.ShouldEmit(100, "Editor"));
    }

    [Fact]
    public void Pid_change_with_same_title_emits()
    {
        var tracker = new FocusTracker();

        Assert.True(tracker.ShouldEmit(100, "Editor"));
        Assert.True(tracker.ShouldEmit(200, "Editor")); // другой процесс - то же окно по заголовку
    }

    [Fact]
    public void Title_change_with_same_pid_emits()
    {
        var tracker = new FocusTracker();

        Assert.True(tracker.ShouldEmit(100, "readme.md - Code"));
        Assert.True(tracker.ShouldEmit(100, "main.cs - Code"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_title_never_emits(string? title)
    {
        var tracker = new FocusTracker();

        Assert.False(tracker.ShouldEmit(100, title));
    }

    [Fact]
    public void Empty_title_does_not_clobber_state()
    {
        var tracker = new FocusTracker();

        Assert.True(tracker.ShouldEmit(100, "Editor"));
        Assert.False(tracker.ShouldEmit(100, ""));       // не смогли прочитать - пропуск
        Assert.False(tracker.ShouldEmit(100, "Editor")); // состояние не испорчено: повтор не эмитится
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_pid_never_emits(int pid)
    {
        var tracker = new FocusTracker();

        Assert.False(tracker.ShouldEmit(pid, "Editor"));
    }

    [Fact]
    public void Reset_makes_next_state_fresh()
    {
        var tracker = new FocusTracker();

        Assert.True(tracker.ShouldEmit(100, "Editor"));
        tracker.Reset();
        Assert.True(tracker.ShouldEmit(100, "Editor")); // после сброса - снова первое наблюдение
    }
}
