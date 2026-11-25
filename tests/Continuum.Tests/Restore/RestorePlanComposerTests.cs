using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Restore;
using Continuum.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Continuum.Tests.Restore;

/// <summary>
/// Хосты плана восстановления из фактов сессии (ФТ-6.1): приложения сессии
/// по имени процесса, файлы с позицией курсора - хостом «code -r -g».
/// </summary>
public sealed class RestorePlanComposerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "continuum-composer-tests", Guid.NewGuid().ToString("N"));

    public RestorePlanComposerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // уборка - best effort
        }
    }

    private sealed class StubResolver(string? codePath) : IApplicationResolver
    {
        public string? ResolveExecutable(string applicationName) => codePath;
    }

    private string CreateFile(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "content");
        return path;
    }

    [Fact]
    public async Task Hosts_are_built_from_session_applications()
    {
        var repository = new FakeRepository();
        var sessionId = await repository.CreateSessionAsync(
            new Session(0, T0, null, SessionStatus.Running, null, 900));
        var notepadId = await repository.UpsertApplicationAsync(
            new ApplicationInfo(0, "Блокнот", @"C:\Windows\notepad.exe", null, T0, T0));
        var explorerId = await repository.UpsertApplicationAsync(
            new ApplicationInfo(0, "Проводник", @"C:\Windows\explorer.exe", null, T0, T0));
        await repository.AppendEventAsync(new Event(0, sessionId, T0, EventKind.AppFocused, notepadId, null, "a.txt - Блокнот", null, null, null));
        await repository.AppendEventAsync(new Event(0, sessionId, T0.AddMinutes(1), EventKind.AppFocused, explorerId, null, "Папка", null, null, null));
        await repository.AppendEventAsync(new Event(0, sessionId, T0.AddMinutes(2), EventKind.AppFocused, notepadId, null, "b.txt - Блокнот", null, null, null));

        var composer = new RestorePlanComposer(repository, new StubResolver(null), NullLogger<RestorePlanComposer>.Instance);
        var inputs = await composer.ComposeAsync(sessionId);

        Assert.Equal(2, inputs.Hosts.Count); // без дублей: notepad один раз
        Assert.Contains(inputs.Hosts, h => h.ProcessName == "notepad");
        Assert.Contains(inputs.Hosts, h => h.ProcessName == "explorer");
        Assert.Empty(inputs.FilesCoveredByHosts);
    }

    [Fact]
    public async Task Open_files_with_line_become_code_editor_hosts()
    {
        var repository = new FakeRepository();
        var sessionId = await repository.CreateSessionAsync(
            new Session(0, T0, null, SessionStatus.Running, null, 900));
        var file = CreateFile("report.md");
        await repository.SaveSnapshotAsync(new Snapshot(0, sessionId, T0, SnapshotReason.SessionEnd, 1, "{}")
        {
            OpenFiles =
            [
                new OpenFile(0, 0, file, null, null, 42, T0, "vscode_state"),
                new OpenFile(0, 0, Path.Combine(_dir, "missing.cs"), null, null, 7, T0, "vscode_state"),
            ],
        });

        // code доступен: файл с позицией открывается на той же строке
        var composer = new RestorePlanComposer(repository, new StubResolver(@"C:\tools\code.cmd"), NullLogger<RestorePlanComposer>.Instance);
        var inputs = await composer.ComposeAsync(sessionId);

        var host = Assert.Single(inputs.Hosts);
        Assert.Equal("code", host.ProcessName);
        Assert.Contains($"-r -g \"{file}:42\"", host.Arguments);
        Assert.Contains(file, inputs.FilesCoveredByHosts);
        Assert.Single(inputs.FilesCoveredByHosts); // несуществующий файл не покрывается
    }

    [Fact]
    public async Task Without_code_editor_files_stay_plain_open_steps()
    {
        var repository = new FakeRepository();
        var sessionId = await repository.CreateSessionAsync(
            new Session(0, T0, null, SessionStatus.Running, null, 900));
        var file = CreateFile("report.md");
        await repository.SaveSnapshotAsync(new Snapshot(0, sessionId, T0, SnapshotReason.SessionEnd, 1, "{}")
        {
            OpenFiles = [new OpenFile(0, 0, file, null, null, 42, T0, "vscode_state")],
        });

        var composer = new RestorePlanComposer(repository, new StubResolver(null), NullLogger<RestorePlanComposer>.Instance);
        var inputs = await composer.ComposeAsync(sessionId);

        Assert.Empty(inputs.Hosts);
        Assert.Empty(inputs.FilesCoveredByHosts);
    }

    [Fact]
    public async Task Browser_urls_become_single_chrome_host_when_resolvable()
    {
        var repository = new FakeRepository();
        var sessionId = await repository.CreateSessionAsync(
            new Session(0, T0, null, SessionStatus.Running, null, 900));
        await repository.SaveSnapshotAsync(new Snapshot(0, sessionId, T0, SnapshotReason.SessionEnd, 1, "{}")
        {
            BrowserTabs =
            [
                new BrowserTab(0, 0, 1, "https://a.test/", "A", true, false, BrowserTabSource.Uia),
                new BrowserTab(0, 0, 1, "https://b.test/doc", "B", true, false, BrowserTabSource.Uia),
                new BrowserTab(0, 0, 1, null, "C", false, true, BrowserTabSource.WindowTitle), // без URL
            ],
        });

        var composer = new RestorePlanComposer(repository, new StubResolver(@"C:\tools\chrome.exe"), NullLogger<RestorePlanComposer>.Instance);
        var inputs = await composer.ComposeAsync(sessionId);

        var host = Assert.Single(inputs.Hosts);
        Assert.Equal("chrome", host.ProcessName);
        Assert.Contains("https://a.test/", host.Arguments);
        Assert.Contains("https://b.test/doc", host.Arguments);
        Assert.Empty(inputs.OpenUrls); // хост покрыл URL - OpenUrl-шаги не нужны
    }

    [Fact]
    public async Task Browser_urls_stay_open_url_steps_without_chrome()
    {
        var repository = new FakeRepository();
        var sessionId = await repository.CreateSessionAsync(
            new Session(0, T0, null, SessionStatus.Running, null, 900));
        await repository.SaveSnapshotAsync(new Snapshot(0, sessionId, T0, SnapshotReason.SessionEnd, 1, "{}")
        {
            BrowserTabs = [new BrowserTab(0, 0, 1, "https://a.test/", "A", true, true, BrowserTabSource.Uia)],
        });

        var composer = new RestorePlanComposer(repository, new StubResolver(null), NullLogger<RestorePlanComposer>.Instance);
        var inputs = await composer.ComposeAsync(sessionId);

        Assert.Empty(inputs.Hosts);
        Assert.Equal(["https://a.test/"], inputs.OpenUrls); // браузер по умолчанию
    }

    [Fact]
    public async Task Chrome_host_from_session_apps_gets_urls_in_arguments()
    {
        // Приложение сессии уже дало хост chrome: второго хоста быть не должно,
        // URL вливаются в аргументы существующего хоста (§5.6)
        var repository = new FakeRepository();
        var sessionId = await repository.CreateSessionAsync(
            new Session(0, T0, null, SessionStatus.Running, null, 900));
        var chromeId = await repository.UpsertApplicationAsync(
            new ApplicationInfo(0, "chrome", @"C:\Chrome\chrome.exe", null, T0, T0));
        await repository.AppendEventAsync(new Event(0, sessionId, T0, EventKind.AppFocused, chromeId, null, "A - Google Chrome", null, null, null));
        await repository.SaveSnapshotAsync(new Snapshot(0, sessionId, T0, SnapshotReason.SessionEnd, 1, "{}")
        {
            BrowserTabs = [new BrowserTab(0, 0, chromeId, "https://a.test/", "A", true, true, BrowserTabSource.Uia)],
        });

        var composer = new RestorePlanComposer(repository, new StubResolver(@"C:\tools\chrome.exe"), NullLogger<RestorePlanComposer>.Instance);
        var inputs = await composer.ComposeAsync(sessionId);

        var host = Assert.Single(inputs.Hosts);
        Assert.Equal("chrome", host.ProcessName);
        Assert.Contains("https://a.test/", host.Arguments);
        Assert.Empty(inputs.OpenUrls);
    }
}
