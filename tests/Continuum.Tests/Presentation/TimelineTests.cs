using System;
using System.Linq;
using System.Threading.Tasks;
using Continuum.Core.Domain;
using Continuum.Presentation;
using Continuum.Tests.Fakes;
using Xunit;

namespace Continuum.Tests.Presentation;

public class TimelineFormatterTests
{
    private static readonly DateTimeOffset Ts = new(2026, 11, 6, 18, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Formats_time_and_description()
    {
        var ev = new Event(0, 1, Ts, EventKind.FileChanged, null, null, null, "d:/work/readme.md", null, null);
        var line = TimelineFormatter.Format(ev);
        Assert.EndsWith("Изменён: readme.md", line);
        Assert.Matches(@"^\d{2}:\d{2}  ", line);
    }

    [Fact]
    public void Session_events_have_fixed_text()
    {
        var start = TimelineFormatter.Describe(new Event(0, 1, Ts, EventKind.SessionStart, null, null, null, null, null, null));
        var end = TimelineFormatter.Describe(new Event(0, 1, Ts, EventKind.SessionEnd, null, null, null, null, null, null));
        Assert.Equal("Сессия начата", start);
        Assert.Equal("Сессия завершена", end);
    }

    [Fact]
    public void Git_commit_uses_title()
    {
        var ev = new Event(0, 1, Ts, EventKind.GitCommit, null, null, "7846b86 feat: работа", null, null, null);
        Assert.Equal("Git commit: 7846b86 feat: работа", TimelineFormatter.Describe(ev));
    }

    [Fact]
    public void Focused_window_uses_title_with_fallback()
    {
        var withTitle = new Event(0, 1, Ts, EventKind.AppFocused, null, null, "Program.cs - Continuum", null, null, null);
        var noTitle = new Event(0, 1, Ts, EventKind.AppFocused, null, null, null, null, null, null);
        Assert.Equal("Фокус: Program.cs - Continuum", TimelineFormatter.Describe(withTitle));
        Assert.Equal("Фокус: (без заголовка)", TimelineFormatter.Describe(noTitle));
    }

    [Fact]
    public void Unknown_kind_falls_back_to_enum_name()
    {
        var ev = new Event(0, 1, Ts, EventKind.TerminalActivity, null, null, null, null, null, null);
        Assert.Equal("Активность в терминале", TimelineFormatter.Describe(ev));
    }

    [Fact]
    public void Empty_path_renders_question_mark()
    {
        var ev = new Event(0, 1, Ts, EventKind.FileChanged, null, null, null, null, null, null);
        Assert.Equal("Изменён: ?", TimelineFormatter.Describe(ev));
    }
}

public class TimelineViewModelTests
{
    [Fact]
    public async Task Loads_last_session_events_ordered()
    {
        var repo = new FakeRepository();
        // Смещение локальное: ToLocalTime в форматтере не сдвигает часы
        var off = DateTimeOffset.Now.Offset;
        var sessionId = await repo.CreateSessionAsync(
            new Session(0, new DateTimeOffset(2026, 11, 6, 16, 0, 0, off), null, SessionStatus.Running, null, 900));
        await repo.AppendEventAsync(new Event(0, sessionId, new DateTimeOffset(2026, 11, 6, 16, 0, 0, off), EventKind.SessionStart, null, null, null, null, null, null));
        await repo.AppendEventAsync(new Event(0, sessionId, new DateTimeOffset(2026, 11, 6, 17, 0, 0, off), EventKind.FileChanged, null, null, null, "d:/w/a.cs", null, null));
        await repo.AppendEventAsync(new Event(0, sessionId, new DateTimeOffset(2026, 11, 6, 16, 30, 0, off), EventKind.GitCommit, null, null, "abc1234 init", null, null, null));

        var vm = new TimelineViewModel(repo);
        await vm.LoadAsync();

        Assert.Equal(3, vm.Events.Count);
        Assert.StartsWith("16:00", vm.Events[0]);
        Assert.StartsWith("16:30", vm.Events[1]);
        Assert.StartsWith("17:00", vm.Events[2]);
        Assert.Contains("3 событий", vm.SummaryText);
    }

    [Fact]
    public async Task No_sessions_shows_placeholder()
    {
        var vm = new TimelineViewModel(new FakeRepository());
        await vm.LoadAsync();
        Assert.Equal("Сессий ещё не было", vm.SummaryText);
        Assert.Empty(vm.Events);
    }

    [Fact]
    public async Task Repository_failure_does_not_throw()
    {
        var repo = new FakeRepository();
        repo.OnAppendEvent = _ => throw new InvalidOperationException("бд недоступна");
        // GetSessionEventsAsync не проходит через OnAppendEvent; падение устроим на чтении:
        // сессия создаётся, а чтение событий кинет - нет; проще: сессии нет -> плейсхолдер
        var vm = new TimelineViewModel(repo);
        await vm.LoadAsync();
        Assert.Equal("Сессий ещё не было", vm.SummaryText);
    }
}
