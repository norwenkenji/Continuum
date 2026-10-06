using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Continuum.Infrastructure.Git.Native;

/// <summary>
/// P/Invoke-объявления WinAPI для git-инфраструктуры. Вызывающая сторона
/// обязана обрабатывать ошибки (нулевое возвращаемое значение +
/// <see cref="Marshal.GetLastWin32Error"/>).
/// </summary>
internal static class GitNativeMethods
{
    /// <summary>Ёмкость буфера под целевой путь устройства.</summary>
    internal const int DevicePathBufferCapacity = 1024;

    /// <summary>
    /// Возвращает целевой путь dos-устройства («C:» → «\??\C:», subst-диск
    /// «S:» → «\??\C:\real\base»). Ноль - ошибка или устройство не найдено.
    /// Используется для разрешения subst-дисков в канонической форме пути.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint QueryDosDeviceW(string lpDeviceName, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder lpTargetPath, int ucchMax);
}
