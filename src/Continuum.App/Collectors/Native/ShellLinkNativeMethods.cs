using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Continuum.Collectors.Native;

/// <summary>
/// COM-объявления для разрешения ярлыков (*.lnk) через IShellLink
/// (data-sources §3.2). Не P/Invoke, но правило «нативные объявления
/// отдельно» распространяется и на COM: здесь только объявления,
/// использование - в RecentFilesMonitor.
/// </summary>
internal static class ShellLinkNativeMethods
{
    /// <summary>STGM_READ: открыть ярлык только на чтение.</summary>
    internal const int StgmRead = 0;

    /// <summary>Буфер под целевой путь (длинные пути включительно).</summary>
    internal const int TargetBufferCapacity = 4096;

    /// <summary>COM-класс Shell Link (CLSID_ShellLink).</summary>
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    internal sealed class ShellLinkObject
    {
    }

    /// <summary>
    /// IPersistFile (IID_IPersistFile). Методы объявлены строго в порядке
    /// vtable: GetClassID унаследован от IPersist, далее IsDirty, Load.
    /// Используются только эти три слота, остальные не объявлены.
    /// </summary>
    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPersistFile
    {
        void GetClassID(out Guid pClassID);

        [PreserveSig]
        int IsDirty();

        [PreserveSig]
        int Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
    }

    /// <summary>
    /// IShellLinkW (IID_IShellLinkW). GetPath - первый метод интерфейса,
    /// поэтому частичное объявление корректно: используемый слот на своём
    /// месте, остальные методы не объявлены и не вызываются.
    /// </summary>
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
            int cchMaxPath,
            IntPtr pfd,
            int fFlags);
    }
}
