using Continuum.Core.Domain;

namespace Continuum.Core.Abstractions;

/// <summary>
/// Провайдер вкладок браузера (browser_tabs в снапшоте, шаг 10).
/// Заголовок - всегда (класс A); URL - через UI Automation best-effort
/// (класс C, data-sources §5.2): таймаут, гонки включения accessibility.
/// Недоступность UIA - url_available = 0, а не ошибка. Никогда не бросает.
/// </summary>
public interface IBrowserTabsProvider
{
    /// <summary>
    /// Вкладки окон браузеров на текущий момент (по одной активной на окно).
    /// Пустой список - браузеров нет. Никогда не бросает.
    /// </summary>
    Task<IReadOnlyList<BrowserTabInfo>> GetBrowserTabsAsync(CancellationToken ct = default);
}
