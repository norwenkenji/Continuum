namespace Continuum.Core.Domain;

/// <summary>Статус сессии (sessions.status).</summary>
public enum SessionStatus
{
    Running,
    Ended
}

/// <summary>Причина завершения сессии (sessions.end_reason).</summary>
public enum SessionEndReason
{
    Idle,
    User,
    Shutdown,
    ProcessExit,
    CrashRecovered
}

/// <summary>Вид события хронологии (events.kind).</summary>
public enum EventKind
{
    SessionStart,
    SessionEnd,
    AppFocused,
    AppStarted,
    AppExited,
    FileChanged,
    FileOpened,
    GitCommit,
    GitBranchSwitch,
    TerminalActivity,
    BrowserNavigate,
    SystemSleep,
    SystemWake
}

/// <summary>Вид изменения файла (file_activity.change_kind).</summary>
public enum FileChangeKind
{
    Modified,
    Added,
    Deleted,
    Untracked,

    /// <summary>Файл открыт (источник Recent\*.lnk; data-sources §3.2: opened, не modified).</summary>
    Opened
}

/// <summary>Источник факта активности файла (file_activity.source).</summary>
public enum FileActivitySource
{
    Git,
    Watcher,
    Recent,
    VsCodeHistory
}

/// <summary>Причина снятия снапшота (snapshots.reason).</summary>
public enum SnapshotReason
{
    Timer,
    User,
    Shutdown,
    SessionEnd
}

/// <summary>Способ получения CWD терминала (terminals.source).</summary>
public enum TerminalSource
{
    CmdLine,
    Peb,
    Unknown
}

/// <summary>Источник данных о вкладке браузера (browser_tabs.source).</summary>
public enum BrowserTabSource
{
    WindowTitle,
    Uia,
    Unavailable
}

/// <summary>Статус плана восстановления (restore_plans.status).</summary>
public enum RestorePlanStatus
{
    Pending,
    Success,
    Partial,
    Failed
}

/// <summary>Результат шага восстановления (restore_steps.result).</summary>
public enum RestoreStepResult
{
    Success,
    Partial,
    Failed,
    Skipped
}

/// <summary>Стратегия шага восстановления (restore_steps.strategy).</summary>
public enum RestoreStrategyKind
{
    StartProcess,
    OpenFile,
    OpenUrl,
    ExecuteCommand,
    CustomAdapter
}
