using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Continuum.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "applications",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    exe_path = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    adapter_key = table.Column<string>(type: "TEXT", nullable: true),
                    first_seen = table.Column<long>(type: "INTEGER", nullable: true),
                    last_seen = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_applications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    root_path = table.Column<string>(type: "TEXT", nullable: false),
                    git_remote = table.Column<string>(type: "TEXT", nullable: true),
                    first_seen = table.Column<long>(type: "INTEGER", nullable: true),
                    last_seen = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    started_at = table.Column<long>(type: "INTEGER", nullable: false),
                    ended_at = table.Column<long>(type: "INTEGER", nullable: true),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    end_reason = table.Column<string>(type: "TEXT", nullable: true),
                    idle_threshold_s = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "settings",
                columns: table => new
                {
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    value = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settings", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    session_id = table.Column<long>(type: "INTEGER", nullable: false),
                    ts = table.Column<long>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    application_id = table.Column<long>(type: "INTEGER", nullable: true),
                    project_id = table.Column<long>(type: "INTEGER", nullable: true),
                    title = table.Column<string>(type: "TEXT", nullable: true),
                    path = table.Column<string>(type: "TEXT", nullable: true),
                    url = table.Column<string>(type: "TEXT", nullable: true),
                    meta_json = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_events_applications_application_id",
                        column: x => x.application_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_events_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_events_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "file_activity",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    session_id = table.Column<long>(type: "INTEGER", nullable: false),
                    ts = table.Column<long>(type: "INTEGER", nullable: false),
                    project_id = table.Column<long>(type: "INTEGER", nullable: true),
                    path = table.Column<string>(type: "TEXT", nullable: false),
                    change_kind = table.Column<string>(type: "TEXT", nullable: false),
                    source = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_activity", x => x.id);
                    table.ForeignKey(
                        name: "FK_file_activity_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_file_activity_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "snapshots",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    session_id = table.Column<long>(type: "INTEGER", nullable: false),
                    ts = table.Column<long>(type: "INTEGER", nullable: false),
                    reason = table.Column<string>(type: "TEXT", nullable: false),
                    schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    summary_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_snapshots", x => x.id);
                    table.ForeignKey(
                        name: "FK_snapshots_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "browser_tabs",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    snapshot_id = table.Column<long>(type: "INTEGER", nullable: false),
                    application_id = table.Column<long>(type: "INTEGER", nullable: false),
                    url = table.Column<string>(type: "TEXT", nullable: true),
                    title = table.Column<string>(type: "TEXT", nullable: true),
                    url_available = table.Column<bool>(type: "INTEGER", nullable: false),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false),
                    source = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_browser_tabs", x => x.id);
                    table.ForeignKey(
                        name: "FK_browser_tabs_applications_application_id",
                        column: x => x.application_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_browser_tabs_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "git_state",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    snapshot_id = table.Column<long>(type: "INTEGER", nullable: false),
                    project_id = table.Column<long>(type: "INTEGER", nullable: false),
                    available = table.Column<bool>(type: "INTEGER", nullable: false),
                    branch = table.Column<string>(type: "TEXT", nullable: true),
                    head_commit = table.Column<string>(type: "TEXT", nullable: true),
                    head_subject = table.Column<string>(type: "TEXT", nullable: true),
                    head_ts = table.Column<long>(type: "INTEGER", nullable: true),
                    dirty_count = table.Column<int>(type: "INTEGER", nullable: false),
                    dirty_files_json = table.Column<string>(type: "TEXT", nullable: true),
                    untracked_count = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_git_state", x => x.id);
                    table.ForeignKey(
                        name: "FK_git_state_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_git_state_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "open_files",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    snapshot_id = table.Column<long>(type: "INTEGER", nullable: false),
                    path = table.Column<string>(type: "TEXT", nullable: false),
                    project_id = table.Column<long>(type: "INTEGER", nullable: true),
                    application_id = table.Column<long>(type: "INTEGER", nullable: true),
                    line = table.Column<int>(type: "INTEGER", nullable: true),
                    last_modified = table.Column<long>(type: "INTEGER", nullable: true),
                    source = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_open_files", x => x.id);
                    table.ForeignKey(
                        name: "FK_open_files_applications_application_id",
                        column: x => x.application_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_open_files_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_open_files_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "restore_plans",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    snapshot_id = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    dry_run = table.Column<bool>(type: "INTEGER", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    started_at = table.Column<long>(type: "INTEGER", nullable: true),
                    finished_at = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_restore_plans", x => x.id);
                    table.ForeignKey(
                        name: "FK_restore_plans_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "terminals",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    snapshot_id = table.Column<long>(type: "INTEGER", nullable: false),
                    available = table.Column<bool>(type: "INTEGER", nullable: false),
                    host = table.Column<string>(type: "TEXT", nullable: false),
                    shell = table.Column<string>(type: "TEXT", nullable: true),
                    cwd = table.Column<string>(type: "TEXT", nullable: true),
                    source = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_terminals", x => x.id);
                    table.ForeignKey(
                        name: "FK_terminals_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "restore_steps",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    plan_id = table.Column<long>(type: "INTEGER", nullable: false),
                    seq = table.Column<int>(type: "INTEGER", nullable: false),
                    strategy = table.Column<string>(type: "TEXT", nullable: false),
                    target = table.Column<string>(type: "TEXT", nullable: true),
                    args_json = table.Column<string>(type: "TEXT", nullable: true),
                    result = table.Column<string>(type: "TEXT", nullable: true),
                    error = table.Column<string>(type: "TEXT", nullable: true),
                    duration_ms = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_restore_steps", x => x.id);
                    table.ForeignKey(
                        name: "FK_restore_steps_restore_plans_plan_id",
                        column: x => x.plan_id,
                        principalTable: "restore_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_applications_name_exe",
                table: "applications",
                columns: new[] { "name", "exe_path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_browser_tabs_application_id",
                table: "browser_tabs",
                column: "application_id");

            migrationBuilder.CreateIndex(
                name: "IX_browser_tabs_snapshot_id",
                table: "browser_tabs",
                column: "snapshot_id");

            migrationBuilder.CreateIndex(
                name: "IX_events_application_id",
                table: "events",
                column: "application_id");

            migrationBuilder.CreateIndex(
                name: "IX_events_project_id",
                table: "events",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_events_session_ts",
                table: "events",
                columns: new[] { "session_id", "ts" });

            migrationBuilder.CreateIndex(
                name: "ix_events_ts",
                table: "events",
                column: "ts");

            migrationBuilder.CreateIndex(
                name: "IX_file_activity_project_id",
                table: "file_activity",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_fileact_path",
                table: "file_activity",
                column: "path");

            migrationBuilder.CreateIndex(
                name: "ix_fileact_session_ts",
                table: "file_activity",
                columns: new[] { "session_id", "ts" });

            migrationBuilder.CreateIndex(
                name: "IX_git_state_project_id",
                table: "git_state",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_git_state_snapshot_id",
                table: "git_state",
                column: "snapshot_id");

            migrationBuilder.CreateIndex(
                name: "IX_open_files_application_id",
                table: "open_files",
                column: "application_id");

            migrationBuilder.CreateIndex(
                name: "IX_open_files_project_id",
                table: "open_files",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_open_files_snapshot_id",
                table: "open_files",
                column: "snapshot_id");

            migrationBuilder.CreateIndex(
                name: "IX_projects_root_path",
                table: "projects",
                column: "root_path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_restore_plans_snapshot_id",
                table: "restore_plans",
                column: "snapshot_id");

            migrationBuilder.CreateIndex(
                name: "ix_restore_steps_plan",
                table: "restore_steps",
                columns: new[] { "plan_id", "seq" });

            migrationBuilder.CreateIndex(
                name: "ix_snapshots_session",
                table: "snapshots",
                columns: new[] { "session_id", "ts" });

            migrationBuilder.CreateIndex(
                name: "IX_terminals_snapshot_id",
                table: "terminals",
                column: "snapshot_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "browser_tabs");

            migrationBuilder.DropTable(
                name: "events");

            migrationBuilder.DropTable(
                name: "file_activity");

            migrationBuilder.DropTable(
                name: "git_state");

            migrationBuilder.DropTable(
                name: "open_files");

            migrationBuilder.DropTable(
                name: "restore_steps");

            migrationBuilder.DropTable(
                name: "settings");

            migrationBuilder.DropTable(
                name: "terminals");

            migrationBuilder.DropTable(
                name: "applications");

            migrationBuilder.DropTable(
                name: "projects");

            migrationBuilder.DropTable(
                name: "restore_plans");

            migrationBuilder.DropTable(
                name: "snapshots");

            migrationBuilder.DropTable(
                name: "sessions");
        }
    }
}
