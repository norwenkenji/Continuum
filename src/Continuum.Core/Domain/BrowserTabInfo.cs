using Continuum.Core.Domain;

namespace Continuum.Core.Abstractions;

/// <summary>
/// Вкладка браузера без привязки к справочнику приложений: ApplicationId
/// проставляет Snapshotter апсертом приложения браузера (browser_tabs.
/// application_id NOT NULL). Шов нужен, чтобы провайдер не знал про БД.
/// </summary>
public sealed record BrowserTabInfo(
    /// <summary>Имя процесса браузера без расширения: chrome, msedge, firefox.</summary>
    string BrowserName,

    /// <summary>Путь к exe браузера (для справочника и исключений приложений).</summary>
    string? ExePath,

    /// <summary>Заголовок активной вкладки (суффикс браузера отсечён).</summary>
    string? Title,

    /// <summary>URL вкладки; null - URL не получен (UIA недоступен).</summary>
    string? Url,

    /// <summary>Получен ли точный URL (BY_HOST правила применяются только тогда).</summary>
    bool UrlAvailable,

    /// <summary>Окно браузера на переднем плане.</summary>
    bool IsActive,

    /// <summary>Источник: window_title (только заголовок) или uia (с URL).</summary>
    BrowserTabSource Source)
{
    /// <summary>Сводная запись для отображения/восстановления.</summary>
    public string? DisplayTitle => Title;
}
