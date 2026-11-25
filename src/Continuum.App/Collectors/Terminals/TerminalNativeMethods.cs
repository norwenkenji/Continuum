using System;
using System.Runtime.InteropServices;

namespace Continuum.Collectors;

/// <summary>
/// P/Invoke чтения процессов и PEB (data-sources §6.2). Весь нативный код
/// терминального коллектора собран здесь; вызовы рассчитаны на x64-цели
/// (Continuum собирается x64, смещения PEB - для 64-битных процессов).
/// Вызывающая сторона обрабатывает отказы (false/Zero/отрицательный NTSTATUS)
/// и освобождает дескрипторы через CloseHandle.
/// </summary>
internal static class TerminalNativeMethods
{
    /// <summary>TH32CS_SNAPPROCESS: снимок списка процессов.</summary>
    internal const uint Th32csSnapProcess = 0x00000002;

    /// <summary>PROCESS_QUERY_INFORMATION | PROCESS_VM_READ: чтение PEB чужого процесса своего пользователя.</summary>
    internal const uint ProcessQueryVmRead = 0x0410;

    /// <summary>PROCESS_BASIC_INFORMATION - класс информации NtQueryInformationProcess.</summary>
    internal const int ProcessBasicInformation = 0;

    /// <summary>Запись снимка процессов (Process32FirstW/Process32NextW).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PROCESSENTRY32W
    {
        /// <summary>Размер структуры в байтах - заполняется до вызова.</summary>
        internal uint dwSize;

        internal uint cntUsage;

        /// <summary>Идентификатор процесса (PID).</summary>
        internal uint th32ProcessID;

        internal UIntPtr th32DefaultHeapID;

        internal uint th32ModuleID;

        internal uint cntThreads;

        /// <summary>PID родительского процесса.</summary>
        internal uint th32ParentProcessID;

        internal int pcPriClassBase;

        internal uint dwFlags;

        /// <summary>Имя exe-файла процесса (например, «cmd.exe»).</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string szExeFile;
    }

    /// <summary>Снимок процессов; при ошибке INVALID_HANDLE_VALUE (-1).</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    /// <summary>Первая запись снимка; поле dwSize заполнить заранее.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    /// <summary>Следующая запись снимка; false - записи кончились или ошибка.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    /// <summary>Открывает процесс; Zero - нет доступа (AppContainer, повышение) или процесс завершён.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    /// <summary>Читает память чужого процесса; false - отказ (гонка завершения, защита).</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ReadProcessMemory(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        byte[] lpBuffer,
        IntPtr nSize,
        out IntPtr lpNumberOfBytesRead);

    /// <summary>NtQueryInformationProcess; возвращает NTSTATUS (0 - успех, отрицательное - отказ).</summary>
    [DllImport("ntdll.dll")]
    internal static extern int NtQueryInformationProcess(
        IntPtr hProcess,
        int processInformationClass,
        byte[] processInformation,
        int processInformationLength,
        IntPtr returnLength);

    /// <summary>Закрывает дескриптор ядра.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);
}
