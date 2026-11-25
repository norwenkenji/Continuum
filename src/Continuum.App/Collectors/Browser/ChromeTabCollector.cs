using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Microsoft.Extensions.Logging;
using System.Windows.Automation;

namespace Continuum.Collectors;

/// <summary>Окно браузера для швов тестирования: имя, pid, hwnd, фокус.</summary>
public sealed record WindowHit(string Name, int ProcessId, int Hwnd, bool HasKeyboardFocus);

/// <summary>
/// Коллектор вкладок браузера (data-sources §5): заголовки - всегда, класс A
/// (перечисление окон UIA); URL - через UI Automation, класс C best-effort:
/// таймаут 1 с по умолчанию, кэш по (hwnd, title) на 2 с, не более одного
/// UIA-обращения к окну за 2 с. Firefox - только заголовок, к UIA не обращаемся
/// (§5.5). Любой сбой COM/UIA - url_available = 0, не ошибка. По одной строке
/// на окно (активная вкладка окна). Никогда не бросает; браузеров нет -
/// пустой список. Опрашивается Snapshotter'ом на момент снапшота.
/// </summary>
public sealed class ChromeTabCollector : IBrowserTabsProvider
{
    /// <summary>Таймаут чтения URL (§5.2: 1 с).</summary>
    private static readonly TimeSpan DefaultUrlTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Окно кэширования URL: повторное обращение не раньше, чем через 2 с.</summary>
    private static readonly TimeSpan UrlCacheWindow = TimeSpan.FromSeconds(2);

    private readonly ILogger<ChromeTabCollector> _logger;
    private readonly TimeSpan _urlTimeout;
    private readonly TimeSpan _urlCacheWindow;
    private readonly Func<IReadOnlyList<WindowHit>>? _enumerateWindows;
    private readonly Func<WindowHit, string?>? _readUrl;

    /// <summary>Все вызовы живого UIA - под одним локом: COM не любит конкурентность.</summary>
    private readonly object _uiaLock = new();

    /// <summary>Кэш URL: (hwnd, title) -&gt; (время обращения, url). Title в ключе: сменился - URL перечитывается.</summary>
    private readonly Dictionary<(int Hwnd, string Title), (long At, string? Url)> _urlCache = new();

    /// <summary>Суффиксы заголовков окон браузеров (§5.1, §5.5): имя браузера и способ.</summary>
    private static readonly (string Suffix, string Browser)[] BrowserSuffixes =
    [
        (" - Google Chrome", "chrome"),
        (" - Microsoft Edge", "msedge"),
        (" - Mozilla Firefox", "firefox"),
    ];

    public ChromeTabCollector(
        ILogger<ChromeTabCollector> logger,
        TimeSpan? urlTimeout = null,
        Func<IReadOnlyList<WindowHit>>? enumerateWindows = null,
        Func<WindowHit, string?>? readUrl = null,
        TimeSpan? urlCacheWindow = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (urlTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(urlTimeout), timeout, "Таймаут чтения URL должен быть положительным.");
        }

        _logger = logger;
        _urlTimeout = urlTimeout ?? DefaultUrlTimeout;
        _urlCacheWindow = urlCacheWindow ?? UrlCacheWindow;
        _enumerateWindows = enumerateWindows;
        _readUrl = readUrl;
    }

    /// <summary>
    /// Вкладки окон браузеров на текущий момент (по одной активной на окно).
    /// Пустой список - браузеров нет или перечисление не удалось: не ошибка.
    /// Никогда не бросает.
    /// </summary>
    public async Task<IReadOnlyList<BrowserTabInfo>> GetBrowserTabsAsync(CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(Collect, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Сбор вкладок браузера не удался - снапшот без browser_tabs");
            return [];
        }
    }

    private IReadOnlyList<BrowserTabInfo> Collect()
    {
        var result = new List<BrowserTabInfo>();
        var windows = EnumerateWindowsSafe();
        foreach (var hit in windows)
        {
            var match = MatchBrowser(hit.Name);
            if (match is null)
            {
                continue; // постороннее окно
            }

            var (suffix, browserName) = match.Value;
            var title = StripSuffix(hit.Name, suffix);
            var exePath = TryGetExePath(hit.ProcessId);

            // Firefox - только заголовок (§5.5): к UIA не обращаемся вообще
            var url = browserName == "firefox" ? null : ReadUrlCached(hit, title);
            result.Add(new BrowserTabInfo(
                BrowserName: browserName,
                ExePath: exePath,
                Title: title,
                Url: url,
                UrlAvailable: url is not null,
                IsActive: hit.HasKeyboardFocus,
                Source: url is not null ? BrowserTabSource.Uia : BrowserTabSource.WindowTitle));
        }

        return result;
    }

    /// <summary>
    /// URL с кэшем (§5.2): то же окно и тот же title в пределах 2 с - берём из
    /// кэша, к UIA не обращаемся. Чтение - с таймаутом: задача не дождалась -
    /// url = null (Chromium включает accessibility-дерево асинхронно).
    /// </summary>
    private string? ReadUrlCached(WindowHit hit, string title)
    {
        var key = (hit.Hwnd, title);
        var now = Environment.TickCount64;
        if (_urlCache.TryGetValue(key, out var cached)
            && now - cached.At < _urlCacheWindow.TotalMilliseconds)
        {
            return cached.Url;
        }

        string? url = null;
        try
        {
            var task = Task.Run(() => ReadUrlSafe(hit));
            if (task.Wait(_urlTimeout))
            {
                url = task.Result;
            }
        }
        catch
        {
            url = null; // COM/UIA исключение - url_available = 0 (спека §5.2)
        }

        _urlCache[key] = (now, url);
        return url;
    }

    /// <summary>Обёртка чтения: шов теста или живой UIA; исключения глотаются.</summary>
    private string? ReadUrlSafe(WindowHit hit)
    {
        try
        {
            if (_readUrl is not null)
            {
                return _readUrl(hit);
            }

            return ReadUrlLive(hit);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Чтение URL окна {Hwnd} не удалось", hit.Hwnd);
            return null;
        }
    }

    /// <summary>Живое чтение: элемент окна по hwnd, поиск адресной строки. Под локом - COM.</summary>
    private string? ReadUrlLive(WindowHit hit)
    {
        lock (_uiaLock)
        {
            var element = AutomationElement.FromHandle(new IntPtr(hit.Hwnd));
            return FindUrl(element);
        }
    }

    /// <summary>
    /// Поиск URL: элементы с ValuePattern в поддереве окна; первый, чей Value -
    /// абсолютный http/https URL. Any exception - null (класс C, §5.2).
    /// </summary>
    private static string? FindUrl(AutomationElement windowElement)
    {
        try
        {
            var candidates = windowElement.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true));
            foreach (AutomationElement candidate in candidates)
            {
                string? value;
                try
                {
                    if (candidate.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                        && pattern is ValuePattern valuePattern)
                    {
                        value = valuePattern.Current.Value;
                    }
                    else
                    {
                        continue;
                    }
                }
                catch
                {
                    continue; // элемент исчез/недоступен - следующий
                }

                if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    return value;
                }
            }
        }
        catch
        {
            // Дерево ещё не построено или COM-отказ - URL недоступен, это норма
        }

        return null;
    }

    /// <summary>Перечисление окон верхнего уровня: шов теста или живой UIA под локом.</summary>
    private IReadOnlyList<WindowHit> EnumerateWindowsSafe()
    {
        try
        {
            if (_enumerateWindows is not null)
            {
                return _enumerateWindows();
            }

            lock (_uiaLock)
            {
                var children = AutomationElement.RootElement.FindAll(
                    TreeScope.Children, Condition.TrueCondition);
                var hits = new List<WindowHit>(children.Count);
                foreach (AutomationElement element in children)
                {
                    var name = element.Current.Name;
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    hits.Add(new WindowHit(
                        name,
                        element.Current.ProcessId,
                        element.Current.NativeWindowHandle,
                        element.Current.HasKeyboardFocus));
                }

                return hits;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Перечисление окон не удалось - вкладок нет");
            return [];
        }
    }

    /// <summary>Путь exe браузера; null при отказе (чужой/повышенный процесс, гонка завершения).</summary>
    private static string? TryGetExePath(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Матч окна по суффиксу заголовка (OrdinalIgnoreCase); null - не браузер.</summary>
    private static (string Suffix, string Browser)? MatchBrowser(string name)
    {
        foreach (var (suffix, browser) in BrowserSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return (suffix, browser);
            }
        }

        return null;
    }

    private static string StripSuffix(string name, string suffix) =>
        name[..^suffix.Length].TrimEnd();
}
