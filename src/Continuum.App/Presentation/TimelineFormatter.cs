using System;
using Continuum.Core.Domain;

namespace Continuum.Presentation;

/// <summary>
/// Форматирование события хронологии в строку UI (шаг 8: «Хронология»).
/// Чистая функция без зависимостей - покрывается юнит-тестами без UI.
/// Время выводится локальное (пользователь смотрит свою смену), по
/// kind - человекочитаемое описание с title/path, где они есть.
/// </summary>
public static class TimelineFormatter
{
    /// <summary>«19:04  Git commit: 7846b86 feat: работа» - одна строка списка.</summary>
    public static string Format(Event ev)
    {
        ArgumentNullException.ThrowIfNull(ev);

        var time = ev.Ts.ToLocalTime().ToString("HH:mm");
        return $"{time}  {Describe(ev)}";
    }

    /// <summary>Человекочитаемое описание события без времени.</summary>
    public static string Describe(Event ev) => ev.Kind switch
    {
        EventKind.SessionStart => "Сессия начата",
        EventKind.SessionEnd => "Сессия завершена",
        EventKind.AppFocused => $"Фокус: {ev.Title ?? "(без заголовка)"}",
        EventKind.AppStarted => $"Запущено: {ev.Title ?? ShortPath(ev.Path) ?? "приложение"}",
        EventKind.AppExited => "Приложение закрыто",
        EventKind.FileChanged => $"Изменён: {ShortPath(ev.Path) ?? "?"}",
        EventKind.FileOpened => $"Открыт: {ShortPath(ev.Path) ?? "?"}",
        EventKind.GitCommit => $"Git commit: {ev.Title ?? ""}",
        EventKind.GitBranchSwitch => $"Смена ветки: {ev.Title ?? ""}",
        EventKind.TerminalActivity => "Активность в терминале",
        EventKind.BrowserNavigate => $"Открыт URL: {ev.Url ?? "?"}",
        EventKind.SystemSleep => "Система ушла в сон",
        EventKind.SystemWake => "Система пробудилась",
        _ => ev.Kind.ToString(),
    };

    /// <summary>Имя файла из полного пути (без каталога).</summary>
    public static string? ShortPath(string? path) =>
        string.IsNullOrEmpty(path) ? null : System.IO.Path.GetFileName(path);
}
