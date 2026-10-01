-- ============================================================================
-- V001: начальная схема БД Continuum (13 таблиц + индексы).
-- Источник истины: readme.md, раздел «Схема БД».
--
-- Правила миграций проекта:
--   - только CREATE TABLE IF NOT EXISTS / CREATE INDEX IF NOT EXISTS;
--   - PRAGMA user_version здесь НЕ выставляется - это делает MigrationRunner
--     после коммита транзакции миграции;
--   - DROP запрещён: миграции только добавляют.
-- ============================================================================

-- Настройки приложения (ключ/значение, JSON для списков)
CREATE TABLE IF NOT EXISTS settings (
    key        TEXT PRIMARY KEY,
    value      TEXT NOT NULL,
    updated_at INTEGER NOT NULL
);

-- Приложения.
-- ОСОЗНАННОЕ ОТСТУПЛЕНИЕ от драфта readme: exe_path объявлен
-- TEXT NOT NULL DEFAULT '' (в драфте - nullable).
-- Причина: SqliteRepository.UpsertApplicationAsync уже использует
-- ON CONFLICT(name, exe_path), а с nullable exe_path уникальность
-- не работает, потому что SQLite считает NULL-ы различными.
CREATE TABLE IF NOT EXISTS applications (
    id          INTEGER PRIMARY KEY,
    name        TEXT NOT NULL,
    exe_path    TEXT NOT NULL DEFAULT '',
    adapter_key TEXT,
    first_seen  INTEGER,
    last_seen   INTEGER
);

-- Арбитр уникальности для ON CONFLICT(name, exe_path) в UpsertApplicationAsync
CREATE UNIQUE INDEX IF NOT EXISTS ux_applications_name_exe ON applications(name, exe_path);

-- Проекты: git root уникален
CREATE TABLE IF NOT EXISTS projects (
    id         INTEGER PRIMARY KEY,
    name       TEXT NOT NULL,
    root_path  TEXT NOT NULL UNIQUE,
    git_remote TEXT,
    first_seen INTEGER,
    last_seen  INTEGER
);

-- Сессии работы
CREATE TABLE IF NOT EXISTS sessions (
    id               INTEGER PRIMARY KEY,
    started_at       INTEGER NOT NULL,
    ended_at         INTEGER,
    status           TEXT NOT NULL,   -- running | ended
    end_reason       TEXT,            -- idle | user | shutdown | process_exit | crash_recovered
    idle_threshold_s INTEGER NOT NULL
);

-- Timeline. Append-only: событие пишется на ИЗМЕНЕНИЕ состояния, не по таймеру.
CREATE TABLE IF NOT EXISTS events (
    id             INTEGER PRIMARY KEY,
    session_id     INTEGER NOT NULL REFERENCES sessions(id),
    ts             INTEGER NOT NULL,
    kind           TEXT NOT NULL,
    application_id INTEGER REFERENCES applications(id),
    project_id     INTEGER REFERENCES projects(id),
    title          TEXT,
    path           TEXT,
    url            TEXT,
    meta_json      TEXT
);
-- kind: session_start | session_end | app_focused | app_started | app_exited
--       file_changed | file_opened | git_commit | git_branch_switch
--       terminal_activity | browser_navigate | system_sleep | system_wake

-- «Файлы за сессию» - event-уровень, не snapshot-уровень
CREATE TABLE IF NOT EXISTS file_activity (
    id          INTEGER PRIMARY KEY,
    session_id  INTEGER NOT NULL REFERENCES sessions(id),
    ts          INTEGER NOT NULL,
    project_id  INTEGER REFERENCES projects(id),
    path        TEXT NOT NULL,
    change_kind TEXT NOT NULL,   -- modified | added | deleted | untracked
    source      TEXT NOT NULL    -- git | watcher | recent | vscode_history
);

-- Снапшоты состояния
CREATE TABLE IF NOT EXISTS snapshots (
    id             INTEGER PRIMARY KEY,
    session_id     INTEGER NOT NULL REFERENCES sessions(id),
    ts             INTEGER NOT NULL,
    reason         TEXT NOT NULL,   -- timer | user | shutdown | session_end
    schema_version INTEGER NOT NULL,
    summary_json   TEXT NOT NULL
);

-- Git-состояние проекта на момент снапшота
CREATE TABLE IF NOT EXISTS git_state (
    id               INTEGER PRIMARY KEY,
    snapshot_id      INTEGER NOT NULL REFERENCES snapshots(id),
    project_id       INTEGER NOT NULL REFERENCES projects(id),
    available        INTEGER NOT NULL,   -- 0 = git не найден / не репозиторий
    branch           TEXT,
    head_commit      TEXT,
    head_subject     TEXT,
    head_ts          INTEGER,
    dirty_count      INTEGER,
    dirty_files_json TEXT,
    untracked_count  INTEGER
);

-- Терминалы на момент снапшота
CREATE TABLE IF NOT EXISTS terminals (
    id          INTEGER PRIMARY KEY,
    snapshot_id INTEGER NOT NULL REFERENCES snapshots(id),
    available   INTEGER NOT NULL,
    host        TEXT NOT NULL,   -- WindowsTerminal | conhost | ...
    shell       TEXT,
    cwd         TEXT,
    source      TEXT NOT NULL    -- cmdline | peb | unknown
);

-- Открытые файлы на момент снапшота
CREATE TABLE IF NOT EXISTS open_files (
    id             INTEGER PRIMARY KEY,
    snapshot_id    INTEGER NOT NULL REFERENCES snapshots(id),
    path           TEXT NOT NULL,
    project_id     INTEGER REFERENCES projects(id),
    application_id INTEGER REFERENCES applications(id),
    line           INTEGER,       -- для code -g path:line
    last_modified  INTEGER,
    source         TEXT NOT NULL
);

-- Вкладки браузера на момент снапшота
CREATE TABLE IF NOT EXISTS browser_tabs (
    id             INTEGER PRIMARY KEY,
    snapshot_id    INTEGER NOT NULL REFERENCES snapshots(id),
    application_id INTEGER NOT NULL REFERENCES applications(id),
    url            TEXT,
    title          TEXT,
    url_available  INTEGER NOT NULL,   -- 0 = URL недоступен, есть только title
    is_active      INTEGER NOT NULL,
    source         TEXT NOT NULL       -- window_title | uia | unavailable
);

-- План восстановления сессии
CREATE TABLE IF NOT EXISTS restore_plans (
    id          INTEGER PRIMARY KEY,
    snapshot_id INTEGER NOT NULL REFERENCES snapshots(id),
    created_at  INTEGER NOT NULL,
    dry_run     INTEGER NOT NULL,
    status      TEXT NOT NULL,   -- pending | success | partial | failed
    started_at  INTEGER,
    finished_at INTEGER
);

-- Шаги плана восстановления
CREATE TABLE IF NOT EXISTS restore_steps (
    id          INTEGER PRIMARY KEY,
    plan_id     INTEGER NOT NULL REFERENCES restore_plans(id),
    seq         INTEGER NOT NULL,
    strategy    TEXT NOT NULL,
    target      TEXT,
    args_json   TEXT,
    result      TEXT,            -- success | partial | failed | skipped
    error       TEXT,
    duration_ms INTEGER
);

-- Индексы (readme.md, раздел «Схема БД»)
CREATE INDEX IF NOT EXISTS ix_events_session_ts   ON events(session_id, ts);
CREATE INDEX IF NOT EXISTS ix_events_ts           ON events(ts);
CREATE INDEX IF NOT EXISTS ix_fileact_session_ts  ON file_activity(session_id, ts);
CREATE INDEX IF NOT EXISTS ix_fileact_path        ON file_activity(path);
CREATE INDEX IF NOT EXISTS ix_snapshots_session   ON snapshots(session_id, ts);
CREATE INDEX IF NOT EXISTS ix_restore_steps_plan  ON restore_steps(plan_id, seq);
