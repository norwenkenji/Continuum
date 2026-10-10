using Continuum.Core.Abstractions;
using Continuum.Runtime.Native;

namespace Continuum.Runtime;

/// <summary>
/// Длительность бездействия пользователя по GetLastInputInfo:
/// время последнего ввода против Environment.TickCount (оба - миллисекунды
/// с загрузки системы, одинаковая эпоха и одинаковое 49-дневное оборачивание).
/// </summary>
public sealed class IdleTimeSource : IIdleTimeSource
{
    public TimeSpan GetIdleTime()
    {
        var info = new IdleNativeMethods.LASTINPUTINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<IdleNativeMethods.LASTINPUTINFO>(),
        };
        if (!IdleNativeMethods.GetLastInputInfo(ref info))
        {
            // Не смогли узнать - считаем, что пользователь активен:
            // ложное закрытие сессии по idle хуже, чем отложенное
            return TimeSpan.Zero;
        }

        // uint-арифметика корректна при оборачивании TickCount (~49,7 суток)
        var idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idleMs);
    }
}
