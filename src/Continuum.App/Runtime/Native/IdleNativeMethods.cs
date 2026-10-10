using System.Runtime.InteropServices;

namespace Continuum.Runtime.Native;

/// <summary>
/// P/Invoke для GetLastInputInfo (user32). Правило проекта: P/Invoke
/// объявляется только в файлах *NativeMethods.cs.
/// </summary>
internal static class IdleNativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}
