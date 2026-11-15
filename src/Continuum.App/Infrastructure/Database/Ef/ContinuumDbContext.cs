using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Continuum.Core.Domain;

namespace Continuum.Infrastructure.Database.Ef;

/// <summary>
/// Контекст данных Continuum (Entity Framework Core, провайдер SQLite).
/// Соединение открывается общей фабрикой (WAL, foreign_keys, synchronous),
/// владение им - у контекста: Dispose/DisposeAsync закрывают соединение.
/// Схема БД описывается моделью (OnModelCreating) и создаётся миграциями
/// EF Core; журнал применённых миграций - таблица __EFMigrationsHistory.
/// </summary>
public sealed class ContinuumDbContext : DbContext
{
    private readonly SqliteConnection _connection;

    public ContinuumDbContext(SqliteConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        _connection = connections.OpenConnection();
    }

    public DbSet<SessionRow> Sessions => Set<SessionRow>();

    public DbSet<EventRow> Events => Set<EventRow>();

    public DbSet<FileActivityRow> FileActivity => Set<FileActivityRow>();

    public DbSet<ProjectRow> Projects => Set<ProjectRow>();

    public DbSet<ApplicationRow> Applications => Set<ApplicationRow>();

    public DbSet<SnapshotRow> Snapshots => Set<SnapshotRow>();

    public DbSet<GitStateRow> GitStates => Set<GitStateRow>();

    public DbSet<TerminalRow> Terminals => Set<TerminalRow>();

    public DbSet<OpenFileRow> OpenFiles => Set<OpenFileRow>();

    public DbSet<BrowserTabRow> BrowserTabs => Set<BrowserTabRow>();

    public DbSet<RestorePlanRow> RestorePlans => Set<RestorePlanRow>();

    public DbSet<RestoreStepRow> RestoreSteps => Set<RestoreStepRow>();

    public DbSet<SettingRow> Settings => Set<SettingRow>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlite(_connection);
        }
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<SessionRow>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.StartedAt).HasColumnName("started_at").IsRequired();
            e.Property(x => x.EndedAt).HasColumnName("ended_at");
            e.Property(x => x.Status).HasColumnName("status").HasConversion(EnumConverter<SessionStatus>()).IsRequired();
            e.Property(x => x.EndReason).HasColumnName("end_reason").HasConversion(NullableEnumConverter<SessionEndReason>());
            e.Property(x => x.IdleThresholdSeconds).HasColumnName("idle_threshold_s").IsRequired();
        });

        model.Entity<EventRow>(e =>
        {
            e.ToTable("events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.SessionId).HasColumnName("session_id").IsRequired();
            e.Property(x => x.Ts).HasColumnName("ts").IsRequired();
            e.Property(x => x.Kind).HasColumnName("kind").HasConversion(EnumConverter<EventKind>()).IsRequired();
            e.Property(x => x.ApplicationId).HasColumnName("application_id");
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.Title).HasColumnName("title");
            e.Property(x => x.Path).HasColumnName("path");
            e.Property(x => x.Url).HasColumnName("url");
            e.Property(x => x.MetaJson).HasColumnName("meta_json");
            e.HasOne<SessionRow>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationRow>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProjectRow>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.SessionId, x.Ts }).HasDatabaseName("ix_events_session_ts");
            e.HasIndex(x => x.Ts).HasDatabaseName("ix_events_ts");
        });

        model.Entity<FileActivityRow>(e =>
        {
            e.ToTable("file_activity");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.SessionId).HasColumnName("session_id").IsRequired();
            e.Property(x => x.Ts).HasColumnName("ts").IsRequired();
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.Path).HasColumnName("path").IsRequired();
            e.Property(x => x.ChangeKind).HasColumnName("change_kind").HasConversion(EnumConverter<FileChangeKind>()).IsRequired();
            e.Property(x => x.Source).HasColumnName("source").HasConversion(EnumConverter<FileActivitySource>()).IsRequired();
            e.HasOne<SessionRow>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProjectRow>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.SessionId, x.Ts }).HasDatabaseName("ix_fileact_session_ts");
            e.HasIndex(x => x.Path).HasDatabaseName("ix_fileact_path");
        });

        model.Entity<ProjectRow>(e =>
        {
            e.ToTable("projects");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name").IsRequired();
            e.Property(x => x.RootPath).HasColumnName("root_path").IsRequired();
            e.Property(x => x.GitRemote).HasColumnName("git_remote");
            e.Property(x => x.FirstSeen).HasColumnName("first_seen");
            e.Property(x => x.LastSeen).HasColumnName("last_seen");
            e.HasIndex(x => x.RootPath).IsUnique();
        });

        model.Entity<ApplicationRow>(e =>
        {
            e.ToTable("applications");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name").IsRequired();
            e.Property(x => x.ExePath).HasColumnName("exe_path").IsRequired().HasDefaultValue(string.Empty);
            e.Property(x => x.AdapterKey).HasColumnName("adapter_key");
            e.Property(x => x.FirstSeen).HasColumnName("first_seen");
            e.Property(x => x.LastSeen).HasColumnName("last_seen");
            e.HasIndex(x => new { x.Name, x.ExePath }).IsUnique().HasDatabaseName("ux_applications_name_exe");
        });

        model.Entity<SnapshotRow>(e =>
        {
            e.ToTable("snapshots");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.SessionId).HasColumnName("session_id").IsRequired();
            e.Property(x => x.Ts).HasColumnName("ts").IsRequired();
            e.Property(x => x.Reason).HasColumnName("reason").HasConversion(EnumConverter<SnapshotReason>()).IsRequired();
            e.Property(x => x.SchemaVersion).HasColumnName("schema_version").IsRequired();
            e.Property(x => x.SummaryJson).HasColumnName("summary_json").IsRequired();
            e.HasOne<SessionRow>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.SessionId, x.Ts }).HasDatabaseName("ix_snapshots_session");
        });

        model.Entity<GitStateRow>(e =>
        {
            e.ToTable("git_state");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.SnapshotId).HasColumnName("snapshot_id").IsRequired();
            e.Property(x => x.ProjectId).HasColumnName("project_id").IsRequired();
            e.Property(x => x.Available).HasColumnName("available").IsRequired();
            e.Property(x => x.Branch).HasColumnName("branch");
            e.Property(x => x.HeadCommit).HasColumnName("head_commit");
            e.Property(x => x.HeadSubject).HasColumnName("head_subject");
            e.Property(x => x.HeadTs).HasColumnName("head_ts");
            e.Property(x => x.DirtyCount).HasColumnName("dirty_count");
            e.Property(x => x.DirtyFilesJson).HasColumnName("dirty_files_json");
            e.Property(x => x.UntrackedCount).HasColumnName("untracked_count");
            e.HasOne(x => x.Snapshot).WithMany(s => s.GitStates).HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProjectRow>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<TerminalRow>(e =>
        {
            e.ToTable("terminals");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.SnapshotId).HasColumnName("snapshot_id").IsRequired();
            e.Property(x => x.Available).HasColumnName("available").IsRequired();
            e.Property(x => x.Host).HasColumnName("host").IsRequired();
            e.Property(x => x.Shell).HasColumnName("shell");
            e.Property(x => x.Cwd).HasColumnName("cwd");
            e.Property(x => x.Source).HasColumnName("source").HasConversion(EnumConverter<TerminalSource>()).IsRequired();
            e.HasOne(x => x.Snapshot).WithMany(s => s.Terminals).HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<OpenFileRow>(e =>
        {
            e.ToTable("open_files");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.SnapshotId).HasColumnName("snapshot_id").IsRequired();
            e.Property(x => x.Path).HasColumnName("path").IsRequired();
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.ApplicationId).HasColumnName("application_id");
            e.Property(x => x.Line).HasColumnName("line");
            e.Property(x => x.LastModified).HasColumnName("last_modified");
            e.Property(x => x.Source).HasColumnName("source").IsRequired();
            e.HasOne(x => x.Snapshot).WithMany(s => s.OpenFiles).HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProjectRow>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationRow>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<BrowserTabRow>(e =>
        {
            e.ToTable("browser_tabs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.SnapshotId).HasColumnName("snapshot_id").IsRequired();
            e.Property(x => x.ApplicationId).HasColumnName("application_id").IsRequired();
            e.Property(x => x.Url).HasColumnName("url");
            e.Property(x => x.Title).HasColumnName("title");
            e.Property(x => x.UrlAvailable).HasColumnName("url_available").IsRequired();
            e.Property(x => x.IsActive).HasColumnName("is_active").IsRequired();
            e.Property(x => x.Source).HasColumnName("source").HasConversion(EnumConverter<BrowserTabSource>()).IsRequired();
            e.HasOne(x => x.Snapshot).WithMany(s => s.BrowserTabs).HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationRow>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<RestorePlanRow>(e =>
        {
            e.ToTable("restore_plans");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.SnapshotId).HasColumnName("snapshot_id").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.DryRun).HasColumnName("dry_run").IsRequired();
            e.Property(x => x.Status).HasColumnName("status").HasConversion(EnumConverter<RestorePlanStatus>()).IsRequired();
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.FinishedAt).HasColumnName("finished_at");
            e.HasOne<SnapshotRow>().WithMany().HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<RestoreStepRow>(e =>
        {
            e.ToTable("restore_steps");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.PlanId).HasColumnName("plan_id").IsRequired();
            e.Property(x => x.Seq).HasColumnName("seq").IsRequired();
            e.Property(x => x.Strategy).HasColumnName("strategy").HasConversion(EnumConverter<RestoreStrategyKind>()).IsRequired();
            e.Property(x => x.Target).HasColumnName("target");
            e.Property(x => x.ArgsJson).HasColumnName("args_json");
            e.Property(x => x.Result).HasColumnName("result").HasConversion(NullableEnumConverter<RestoreStepResult>());
            e.Property(x => x.Error).HasColumnName("error");
            e.Property(x => x.DurationMs).HasColumnName("duration_ms");
            e.HasOne(x => x.Plan).WithMany(p => p.Steps).HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.PlanId, x.Seq }).HasDatabaseName("ix_restore_steps_plan");
        });

        model.Entity<SettingRow>(e =>
        {
            e.ToTable("settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasColumnName("key");
            e.Property(x => x.Value).HasColumnName("value").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        });
    }

    public override void Dispose()
    {
        base.Dispose();
        _connection.Dispose();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>enum - snake_case строка (EnumCodec): sessions.status = 'running'.</summary>
    private static ValueConverter<TEnum, string> EnumConverter<TEnum>()
        where TEnum : struct, Enum
        => new(v => EnumCodec.ToDb(v), v => EnumCodec.FromDb<TEnum>(v));

    /// <summary>То же для nullable-колонок (end_reason, restore_steps.result).</summary>
    private static ValueConverter<TEnum?, string?> NullableEnumConverter<TEnum>()
        where TEnum : struct, Enum
        => new(
            v => v == null ? null : EnumCodec.ToDb(v.Value),
            v => v == null ? null : EnumCodec.FromDb<TEnum>(v));
}
