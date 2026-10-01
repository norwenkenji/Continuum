using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Continuum.Collectors.Native;

/// <summary>
/// P/Invoke-объявления WinAPI для коллекторов. Весь небезопасный код собран
/// здесь; вызывающая сторона обязана обрабатывать ошибки (false/Zero +
/// <see cref="Marshal.GetLastWin32Error"/>) и освобождать дескрипторы
/// через <see cref="CloseHandle"/>.
/// </summary>
internal static class NativeMethods
{
    /// <summary>Ёмкость буфера заголовка окна; более длинные заголовки усекаем.</summary>
    internal const int TitleBufferCapacity = 512;

    /// <summary>Ёмкость буфера полного пути exe (длинные пути встречаются).</summary>
    internal const int ExePathBufferCapacity = 1024;

    /// <summary>Флаги <see cref="CreateToolhelp32Snapshot"/>.</summary>
    [Flags]
    internal enum SnapshotFlags : uint
    {
        /// <summary>Снапшот списка процессов (TH32CS_SNAPPROCESS).</summary>
        Process = 0x00000002,
    }

    /// <summary>Уровни доступа к процессу (<see cref="OpenProcess"/>).</summary>
    [Flags]
    internal enum ProcessAccess : uint
    {
        /// <summary>Минимальный доступ для чтения пути образа; работает без админки.</summary>
        QueryLimitedInformation = 0x1000,
    }

    /// <summary>Запись снапшота процессов (<see cref="Process32FirstW"/>/<see cref="Process32NextW"/>).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PROCESSENTRY32W
    {
        /// <summary>Размер структуры в байтах - заполняется ДО вызова.</summary>
        internal uint dwSize;

        internal uint cntUsage;

        /// <summary>Идентификатор процесса (PID).</summary>
        internal uint th32ProcessID;

        internal UIntPtr th32DefaultHeapID;

        internal uint th32ModuleID;

        internal uint cntThreads;

        internal uint th32ParentProcessID;

        internal int pcPriClassBase;

        internal uint dwFlags;

        /// <summary>Имя exe-файла процесса (например, «chrome.exe»).</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string szExeFile;
    }

    /// <summary>Дескриптор фокусного окна; Zero, если фокуса нет (экран UAC, блокировка).</summary>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetForegroundWindow();

    /// <summary>Длина заголовка окна в символах, без завершающего нуля.</summary>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLengthW(IntPtr hWnd);

    /// <summary>Читает заголовок окна в буфер; возвращает число скопированных символов.</summary>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextW(IntPtr hWnd, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder lpString, int nMaxCount);

    /// <summary>Идентификаторы потока-владельца окна и его процесса.</summary>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>Снапшот процессов системы. При ошибке возвращает INVALID_HANDLE_VALUE (-1).</summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateToolhelp32Snapshot(SnapshotFlags dwFlags, uint th32ProcessID);

    /// <summary>Первая запись снапшота; поле dwSize структуры заполнить заранее.</summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    /// <summary>Следующая запись снапшота; false - записи кончились или ошибка.</summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    /// <summary>Открывает процесс; Zero - нет доступа или процесс уже завершился (гонка).</summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr OpenProcess(ProcessAccess dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    /// <summary>Полный путь exe-образа процесса; false - нет доступа (системный процесс, гонка).</summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder lpExeName, ref uint lpdwSize);

    /// <summary>Закрывает дескриптор ядра.</summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CloseHandle(IntPtr hObject);
}
