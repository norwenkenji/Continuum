namespace Continuum.Core.Domain;

/// <summary>
/// Точка состояния контекста (snapshots). Реляционные таблицы хранят то,
/// по чему есть запросы; остальное — в SummaryJson. SchemaVersion версионирует
/// формат SummaryJson, а не схему БД (она версионируется через PRAGMA user_version).
/// </summary>
public sealed record Snapshot(
    long Id,
    long SessionId,
    DateTimeOffset Ts,
    SnapshotReason Reason,
    int SchemaVersion,
    string SummaryJson)
{
    public IReadOnlyList<GitState> GitStates { get; init; } = [];

    public IReadOnlyList<TerminalState> Terminals { get; init; } = [];

    public IReadOnlyList<OpenFile> OpenFiles { get; init; } = [];

    public IReadOnlyList<BrowserTab> BrowserTabs { get; init; } = [];
}

/// <summary>Состояние git-репозитория проекта на момент снапшота (git_state).</summary>
public sealed record GitState(
    long Id,
    long SnapshotId,
    long ProjectId,
    bool Available,
    string? Branch,
    string? HeadCommit,
    string? HeadSubject,
    DateTimeOffset? HeadTs,
    int DirtyCount,
    string? DirtyFilesJson,
    int UntrackedCount);

/// <summary>Наблюдаемый терминал на момент снапшота (terminals).</summary>
public sealed record TerminalState(
    long Id,
    long SnapshotId,
    bool Available,
    string Host,
    string? Shell,
    string? Cwd,
    TerminalSource Source);

/// <summary>Открытый файл на момент снапшота (open_files). Line — для code -g path:line.</summary>
public sealed record OpenFile(
    long Id,
    long SnapshotId,
    string Path,
    long? ProjectId,
    long? ApplicationId,
    int? Line,
    DateTimeOffset? LastModified,
    string Source);

/// <summary>Вкладка браузера на момент снапшота (browser_tabs).</summary>
public sealed record BrowserTab(
    long Id,
    long SnapshotId,
    long ApplicationId,
    string? Url,
    string? Title,
    bool UrlAvailable,
    bool IsActive,
    BrowserTabSource Source);
