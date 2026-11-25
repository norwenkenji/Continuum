using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Collectors;
using Continuum.Core.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Continuum.Tests.Collectors;

/// <summary>
/// ChromeTabCollector на швах (enumerateWindows + readUrl): живой UIA не
/// вызывается, тесты детерминированы. Проверяются суффиксы браузеров,
/// обрезка заголовков, honest-деградация URL (null/исключение/таймаут),
/// кэш по (hwnd, title) и полный отказ без исключений.
/// </summary>
public class BrowserTabCollectorTests
{
    [Fact]
    public async Task Chrome_window_with_url_gives_uia_row()
    {
        var collector = Create(
            () => [new WindowHit("Страница - Google Chrome", 4321, 100, false)],
            _ => "https://example.com/page");

        var tabs = await collector.GetBrowserTabsAsync();

        var tab = Assert.Single(tabs);
        Assert.Equal("chrome", tab.BrowserName);
        Assert.Equal("Страница", tab.Title); // суффикс браузера отсечён
        Assert.Equal("https://example.com/page", tab.Url);
        Assert.True(tab.UrlAvailable);
        Assert.Equal(BrowserTabSource.Uia, tab.Source);
        Assert.False(tab.IsActive);
    }

    [Fact]
    public async Task Chrome_window_without_url_gives_window_title_row()
    {
        var collector = Create(
            () => [new WindowHit("Страница - Google Chrome", 4321, 100, false)],
            _ => null);

        var tabs = await collector.GetBrowserTabsAsync();

        var tab = Assert.Single(tabs);
        Assert.Equal("chrome", tab.BrowserName);
        Assert.Null(tab.Url);
        Assert.False(tab.UrlAvailable);
        Assert.Equal(BrowserTabSource.WindowTitle, tab.Source);
    }

    [Fact]
    public async Task Throwing_url_reader_gives_title_only_without_exception()
    {
        var collector = Create(
            () => [new WindowHit("Страница - Google Chrome", 4321, 100, false)],
            _ => throw new InvalidOperationException("COM-отказ"));

        var tabs = await collector.GetBrowserTabsAsync(); // не бросает

        var tab = Assert.Single(tabs);
        Assert.Equal("chrome", tab.BrowserName);
        Assert.Equal("Страница", tab.Title);
        Assert.Null(tab.Url);
        Assert.False(tab.UrlAvailable);
        Assert.Equal(BrowserTabSource.WindowTitle, tab.Source);
    }

    [Fact]
    public async Task Edge_window_is_matched_and_stripped()
    {
        var collector = Create(
            () => [new WindowHit("Документация - Microsoft Edge", 4321, 100, false)],
            _ => "https://learn.microsoft.com/");

        var tabs = await collector.GetBrowserTabsAsync();

        var tab = Assert.Single(tabs);
        Assert.Equal("msedge", tab.BrowserName);
        Assert.Equal("Документация", tab.Title);
        Assert.Equal("https://learn.microsoft.com/", tab.Url);
        Assert.Equal(BrowserTabSource.Uia, tab.Source);
    }

    [Fact]
    public async Task Firefox_window_is_title_only_and_never_touches_uia()
    {
        var urlCalls = 0;
        var collector = Create(
            () => [new WindowHit("Заметки - Mozilla Firefox", 4321, 100, false)],
            _ =>
            {
                urlCalls++;
                return "https://example.com";
            });

        var tabs = await collector.GetBrowserTabsAsync();

        var tab = Assert.Single(tabs);
        Assert.Equal("firefox", tab.BrowserName);
        Assert.Equal("Заметки", tab.Title);
        Assert.Null(tab.Url);
        Assert.False(tab.UrlAvailable);
        Assert.Equal(BrowserTabSource.WindowTitle, tab.Source);
        Assert.Equal(0, urlCalls); // к UIA Firefox не обращается вообще (§5.5)
    }

    [Fact]
    public async Task IsActive_translates_from_keyboard_focus()
    {
        var collector = Create(
            () => [new WindowHit("Страница - Google Chrome", 4321, 100, true)],
            _ => "https://example.com");

        var tabs = await collector.GetBrowserTabsAsync();

        Assert.True(Assert.Single(tabs).IsActive);
    }

    [Fact]
    public async Task Foreign_windows_are_ignored_and_empty_input_gives_empty_output()
    {
        var foreign = await Create(() => [new WindowHit("Текст - Блокнот", 4321, 100, false)])
            .GetBrowserTabsAsync();
        Assert.Empty(foreign);

        var nothing = await Create(() => []).GetBrowserTabsAsync();
        Assert.Empty(nothing);
    }

    [Fact]
    public async Task Url_read_timeout_gives_null_fast()
    {
        var collector = Create(
            () => [new WindowHit("Страница - Google Chrome", 4321, 100, false)],
            _ =>
            {
                Thread.Sleep(300); // UIA-дерево строится дольше таймаута
                return "https://example.com";
            },
            TimeSpan.FromMilliseconds(50));

        var stopwatch = Stopwatch.StartNew();
        var tabs = await collector.GetBrowserTabsAsync();
        stopwatch.Stop();

        var tab = Assert.Single(tabs);
        Assert.Null(tab.Url); // задача не дождалась - url = null
        Assert.False(tab.UrlAvailable);
        Assert.Equal(BrowserTabSource.WindowTitle, tab.Source);
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, "Таймаут не сработал вовремя");
    }

    [Fact]
    public async Task Url_cache_skips_second_read_within_two_seconds()
    {
        var urlCalls = 0;
        var collector = new ChromeTabCollector(
            NullLogger<ChromeTabCollector>.Instance,
            urlTimeout: TimeSpan.FromSeconds(15),
            enumerateWindows: () => [new WindowHit("Страница - Google Chrome", 4321, 100, false)],
            readUrl: _ =>
            {
                urlCalls++;
                return "https://example.com/page";
            },
            // окно кэша больше времени теста: проверка логики, а не гонка с часами
            urlCacheWindow: TimeSpan.FromSeconds(60));

        var first = await collector.GetBrowserTabsAsync();
        var second = await collector.GetBrowserTabsAsync();

        Assert.Equal(1, urlCalls); // то же окно, тот же title - из кэша
        Assert.Equal("https://example.com/page", Assert.Single(first).Url);
        Assert.Equal("https://example.com/page", Assert.Single(second).Url);
        Assert.Equal(BrowserTabSource.Uia, Assert.Single(second).Source);
    }

    private static ChromeTabCollector Create(
        Func<IReadOnlyList<WindowHit>> enumerateWindows,
        Func<WindowHit, string?>? readUrl = null,
        TimeSpan? urlTimeout = null) =>
        // Таймаут по умолчанию для швов - с запасом: под параллельной нагрузкой
        // xunit внутренняя задача может опоздать к 1-секундному Wait продакшн-режима
        new(NullLogger<ChromeTabCollector>.Instance,
            urlTimeout ?? TimeSpan.FromSeconds(15), enumerateWindows, readUrl);
}
