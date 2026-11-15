using System.Collections.Generic;
using Continuum.Core.Domain;

namespace Continuum.Infrastructure.Database.Ef;

// Строки таблиц для Entity Framework Core. Отдельные классы, а не доменные
// record-ы: домен не знает про персистентность (Core без EF), а EF нужны
// изменяемые свойства. Времена хранятся unix-секундами (как в схеме),
// enum-ы - snake_case строкой через EnumCodec (конвертеры в ContinuumDbContext).

/// <summary>sessions: сессия работы.</summary>
public sealed class SessionRow
{
    public long Id { get; set; }

    public long StartedAt { get; set; }

    public long? EndedAt { get; set; }

    public SessionStatus Status { get; set; }

    public SessionEndReason? EndReason { get; set; }

    public int IdleThresholdSeconds { get; set; }
}

/// <summary>events: хронология (append-only).</summary>
public sealed class EventRow
{
    public long Id { get; set; }

    public long SessionId { get; set; }

    public long Ts { get; set; }

    public EventKind Kind { get; set; }

    public long? ApplicationId { get; set; }

    public long? ProjectId { get; set; }

    public string? Title { get; set; }

    public string? Path { get; set; }

    public string? Url { get; set; }

    public string? MetaJson { get; set; }
}

/// <summary>file_activity: активность файлов (event-уровень).</summary>
public sealed class FileActivityRow
{
    public long Id { get; set; }

    public long SessionId { get; set; }

    public long Ts { get; set; }

    public long? ProjectId { get; set; }

    public string Path { get; set; } = string.Empty;

    public FileChangeKind ChangeKind { get; set; }

    public FileActivitySource Source { get; set; }
}

/// <summary>projects: проект (git root уникален).</summary>
public sealed class ProjectRow
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string RootPath { get; set; } = string.Empty;

    public string? GitRemote { get; set; }

    public long? FirstSeen { get; set; }

    public long? LastSeen { get; set; }
}

/// <summary>applications: справочник приложений; уникальность (name, exe_path).</summary>
public sealed class ApplicationRow
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string ExePath { get; set; } = string.Empty;

    public string? AdapterKey { get; set; }

    public long? FirstSeen { get; set; }

    public long? LastSeen { get; set; }
}

/// <summary>snapshots: точка состояния контекста.</summary>
public sealed class SnapshotRow
{
    public long Id { get; set; }

    public long SessionId { get; set; }

    public long Ts { get; set; }

    public SnapshotReason Reason { get; set; }

    public int SchemaVersion { get; set; }

    public string SummaryJson { get; set; } = string.Empty;

    public List<GitStateRow> GitStates { get; } = [];

    public List<TerminalRow> Terminals { get; } = [];

    public List<OpenFileRow> OpenFiles { get; } = [];

    public List<BrowserTabRow> BrowserTabs { get; } = [];
}

/// <summary>git_state: состояние репозитория проекта на момент снапшота.</summary>
public sealed class GitStateRow
{
    public long Id { get; set; }

    public long SnapshotId { get; set; }

    public long ProjectId { get; set; }

    public bool Available { get; set; }

    public string? Branch { get; set; }

    public string? HeadCommit { get; set; }

    public string? HeadSubject { get; set; }

    public long? HeadTs { get; set; }

    public int DirtyCount { get; set; }

    public string? DirtyFilesJson { get; set; }

    public int UntrackedCount { get; set; }

    public SnapshotRow? Snapshot { get; set; }
}

/// <summary>terminals: терминалы на момент снапшота.</summary>
public sealed class TerminalRow
{
    public long Id { get; set; }

    public long SnapshotId { get; set; }

    public bool Available { get; set; }

    public string Host { get; set; } = string.Empty;

    public string? Shell { get; set; }

    public string? Cwd { get; set; }

    public TerminalSource Source { get; set; }

    public SnapshotRow? Snapshot { get; set; }
}

/// <summary>open_files: открытые файлы на момент снапшота (line - для code -g).</summary>
public sealed class OpenFileRow
{
    public long Id { get; set; }

    public long SnapshotId { get; set; }

    public string Path { get; set; } = string.Empty;

    public long? ProjectId { get; set; }

    public long? ApplicationId { get; set; }

    public int? Line { get; set; }

    public long? LastModified { get; set; }

    public string Source { get; set; } = string.Empty;

    public SnapshotRow? Snapshot { get; set; }
}

/// <summary>browser_tabs: вкладки браузера на момент снапшота.</summary>
public sealed class BrowserTabRow
{
    public long Id { get; set; }

    public long SnapshotId { get; set; }

    public long ApplicationId { get; set; }

    public string? Url { get; set; }

    public string? Title { get; set; }

    public bool UrlAvailable { get; set; }

    public bool IsActive { get; set; }

    public BrowserTabSource Source { get; set; }

    public SnapshotRow? Snapshot { get; set; }
}

/// <summary>restore_plans: план восстановления сессии.</summary>
public sealed class RestorePlanRow
{
    public long Id { get; set; }

    public long SnapshotId { get; set; }

    public long CreatedAt { get; set; }

    public bool DryRun { get; set; }

    public RestorePlanStatus Status { get; set; }

    public long? StartedAt { get; set; }

    public long? FinishedAt { get; set; }

    public List<RestoreStepRow> Steps { get; } = [];
}

/// <summary>restore_steps: шаги плана восстановления.</summary>
public sealed class RestoreStepRow
{
    public long Id { get; set; }

    public long PlanId { get; set; }

    public int Seq { get; set; }

    public RestoreStrategyKind Strategy { get; set; }

    public string? Target { get; set; }

    public string? ArgsJson { get; set; }

    public RestoreStepResult? Result { get; set; }

    public string? Error { get; set; }

    public long? DurationMs { get; set; }

    public RestorePlanRow? Plan { get; set; }
}

/// <summary>settings: настройки «ключ - значение».</summary>
public sealed class SettingRow
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public long UpdatedAt { get; set; }
}
