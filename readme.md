# Continuum

### Personal Work Context Engine

**Continuum — локальная система сохранения, анализа и восстановления рабочего контекста.**

> Компьютер умеет восстанавливать открытые окна. Continuum отвечает на другой вопрос: **«Чем человек занимался и на каком этапе остановился?»**

Документы проекта:

| Файл | Что в нём |
| --- | --- |
| `readme.md` | **Источник истины**: скоуп MVP, архитектура, схема БД, порядок работ, демо-сценарий, сборка |
| `docs/specs/privacy-pipeline.md` | Нормативная спецификация приватности: фильтр, санитизация, исключения, retention |
| `docs/specs/data-sources.md` | Нормативная спецификация источников данных: способ получения и ограничения каждого |

При расхождении приоритет: `readme.md` → `docs/specs/*`.

---

## Проблема

Типичная рабочая сессия разработчика:

```text
VS Code
├── Project A
├── Project B
└── несколько файлов

Windows Terminal
├── PowerShell
├── Git
└── CLI-инструменты

Chrome
├── GitHub
├── документация
└── StackOverflow
```

После закрытия приложений или перезагрузки остаются вопросы без ответа:

- над каким проектом шла работа;
- какие файлы изменены;
- что закоммичено, а что нет;
- какие команды выполнялись в терминале;
- какие ресурсы были открыты под задачу;
- где работа прервана и что делать дальше.

Штатное восстановление окон отвечает: **«Что было открыто?»**

Continuum отвечает: **«Чем занимался и где остановился?»**

---

## Решение

```text
Collectors → Privacy pipeline → Context Engine → Session / Timeline / Snapshot
           → Stopped Engine → Restore Engine → RestorePlan
```

Три базовые сущности:

- **Context** — текущее состояние окружения (приложения, окна, пути файлов, URL, терминалы, проекты).
- **Session** — период непрерывной работы с формальными границами. Отвечает: *что происходило?*
- **Snapshot** — точка состояния. Отвечает: *как выглядел контекст в конкретный момент?*

Поверх них:

- **Timeline** — append-only история событий сессии;
- **«Где я остановился?»** — детерминированный ответ: последний проект, последний файл, последний коммит, незакоммиченное после коммита, последний терминал, статус работы;
- **RestorePlan** — упорядоченный план восстановления с результатом каждого шага (`success / partial / failed`).

В MVP всё это **детерминированно, без LLM**. Результат воспроизводим и проверяем юнит-тестами.

---

## Принципы

Зафиксированы и не обсуждаются в рамках MVP:

1. **Обычный пользователь.** Работа без админских прав. Нет UAC, служб, драйверов, записи в HKLM, системных каталогов. Чтение HKLM разрешено (поиск путей приложений).
2. **Portable / zero-setup.** Скопировал папку → запустил `Continuum.exe` → работает. Continuum обязан запускаться и на машине без VS Code, git и Chrome: отсутствие инструмента — это `partial`, а не падение.
3. **User-isolated.** Данные в `%LOCALAPPDATA%\Continuum\`. Профили Windows не смешиваются.
4. **Local-first.** Хранилище — SQLite. Сетевых вызовов у Continuum нет вообще: ни телеметрии, ни облака, ни обновлений через интернет.
5. **UI тонкий.** WPF — только оболочка над `Application / Core`. Бизнес-логики в окнах нет.
6. **Интерфейс на русском.** Все видимые строки, кнопки и статусы — русские. Английскими остаются имена классов и технические термины в коде.
7. **Честная деградация.** Если источник данных не сработал, UI показывает «недоступно». Пустое значение выглядит как баг, выдуманное — как обман; обе ситуации недопустимы.
8. **Невидимая стоимость.** Фоновый рекордер обязан оставаться в пределах бюджета ресурсов (см. ниже).

Стек MVP:

```text
Windows + .NET 10 (net10.0-windows) + WPF + SQLite (Microsoft.Data.Sqlite)
```

---

## Чего Continuum не делает

Граница проекта. Отвечает на вопрос «это же шпион?» до того, как он задан.

```text
НЕ делает скриншоты
НЕ перехватывает клавиатуру и не записывает нажатия
НЕ читает содержимое файлов — только пути, имена и метаданные
НЕ читает содержимое страниц — только title и (когда доступно) URL
НЕ обращается в сеть
НЕ требует административных прав
НЕ работает, когда процесс не запущен (нет службы, нет драйвера)
НЕ объединяет данные разных пользователей Windows
НЕ изменяет репозитории пользователя (нет fetch/pull/push/commit/reset)
```

Данные принадлежат пользователю: одна папка в `%LOCALAPPDATA%`, удаление = удаление папки.

---

## Архитектура

Два проекта. Разделение — не косметика: один csproj делает правило «Core не знает про UI» комментарием, а не проверяемым ограничением.

> Структура из трёх проектов создана на шаге 0: `src/Continuum.Core` (`net10.0`), `src/Continuum.App` (`net10.0-windows`, на выходе `Continuum.exe`), `tests/Continuum.Tests`. Решение — классический `Continuum.sln` (не `.slnx`, чтобы открывался любым VS и SDK).

```text
Continuum.sln
│
├── src/Continuum.Core      (net10.0 — переносимое ядро, без WinAPI и без WPF)
│   ├── Domain            Context, Session, Snapshot, Activity, Event,
│   │                     Application, Project, RestorePlan, RestoreStep
│   ├── Interfaces        IContextCollector, IAdapter, IRestoreStrategy,
│   │                     IRepository, IPrivacyFilter, ISanitizer, IClock
│   └── Engines           ContextEngine, SessionEngine, TimelineEngine,
│                         SnapshotEngine, StoppedEngine, RestoreEngine
│
├── src/Continuum.App       (net10.0-windows — оболочка и инфраструктура)
│   ├── Application       ContextService, SessionService, TimelineService,
│   │                     SnapshotService, RestoreService, ProjectService,
│   │                     SettingsService, DemoService
│   ├── Infrastructure
│   │   ├── Database      SQLite, миграции через PRAGMA user_version
│   │   ├── Windows       ProcessCollector, WindowCollector, SystemEvents
│   │   ├── Files         FileActivityCollector, RecentDocuments
│   │   ├── Git           GitClient (обёртка над git.exe)
│   │   ├── Browser       ChromeTitleCollector, ChromeUrlCollector (UIA)
│   │   ├── Terminal      TerminalCollector (CommandLine через CIM)
│   │   └── Privacy       PrivacyFilter, SecretSanitizer (реализация интерфейсов Core)
│   ├── Adapters          VSCode, Chrome, Git, Terminal  →  растущий список
│   └── Presentation      MainWindow, TimelineWindow, StoppedWindow,
│                         SettingsWindow, FirstRunDialog, TrayIcon
│
└── tests/Continuum.Tests   (net10.0-windows, xunit — включая архитектурный тест,
                             что Core не ссылается на Windows-стеки)
```

### Направление зависимостей

Стрелка «A → B» означает «A зависит от B».

```text
        ┌─────────────┐
        │ Presentation│   WPF — только оболочка
        └──────┬──────┘
               ▼
        ┌─────────────┐
        │ Application │   сервисы и сценарии
        └──────┬──────┘
               ▼
        ┌─────────────┐        реализуют интерфейсы ядра
        │    Core     │◄────── Infrastructure, Adapters
        └─────────────┘
```

Core не зависит ни от кого. `Infrastructure` и `Adapters` **реализуют интерфейсы, объявленные в Core** (инверсия зависимостей): ядро не знает ни про SQLite, ни про WinAPI, ни про Chrome. Отсюда — проверяемый тезис: **ядро переносимо, оболочка заменяема** (`WPF → Avalonia → Web UI → CLI` используют один Core).

### Два механизма против спагетти

**Adapter API** вместо `if Chrome / if VSCode / ...`:

```text
Detect()        → приложение найдено и доступно?
CaptureState()  → что удалось прочитать?
RestoreState()  → что удалось восстановить?
```

`Detect() == false` — нормальный результат, а не исключение. Адаптер сообщает о недоступности явно.

**RestoreStrategy** вместо знания о каждой программе в Core:

```text
StartProcess / OpenFile / OpenURL / ExecuteCommand / CustomAdapter
```

```text
README.md           → OpenFile
GitHub URL          → OpenURL
Windows Terminal    → StartProcess (-d <cwd>)
VS Code на строке   → ExecuteCommand (code -g <path>:<line>)
Сложное приложение  → CustomAdapter
```

`ExecuteCommand` в MVP работает **только по whitelist** шаблонов: строка команды хранится в БД, и выполнение произвольных строк из неё — это выполнение произвольного кода. Разрешены: `code -g <path>[:<line>]`, `wt -d <path>`, фиксированный набор read-only git-команд.

Неизвестное приложение тоже сохраняется и восстанавливается на базовом уровне: exe, рабочая директория, окно (позиция/размер), связанные файлы.

---

## Данные и приватность

Continuum потенциально видит чувствительное: URL, пути файлов, команды, AI-сессии. Поэтому не так:

```text
захват всего → SQLite
```

А так:

```text
Observable → Filter → Sanitize → Persist
```

Конвейер включается **до первой записи в БД**, а не вместе с экраном настроек: сначала пишем защищённо, потом появляется UI для правки правил.

В MVP:

- разрешения коллекторов (какой коллектор что имеет право читать);
- исключения приложений и директорий;
- `Не записывать никогда`: менеджеры паролей, приватный режим браузера — с явной оговоркой о детектировании (см. ниже);
- редактирование секретов в командных строках (токены, пароли, ключи);
- срок хранения (retention) + полное удаление данных, включая `-wal`/`-shm`.

Принцип: **Capture context ≠ capture everything.**

Полная нормативная спецификация — [`docs/specs/privacy-pipeline.md`](docs/specs/privacy-pipeline.md).

**Известные ограничения** (фиксируются честно, а не замалчиваются):

- точный URL вкладки Chrome без расширения браузера получается только через UI Automation и только когда он доступен;
- incognito-окно надёжно не детектируется ни по заголовку, ни по процессу — поэтому гарантия не-записи достигается исключением приложения целиком.

Детали по каждому источнику — [`docs/specs/data-sources.md`](docs/specs/data-sources.md).

---

## MVP Scope

### Must

**Platform**

- Windows, обычный пользователь, без админки.

**Collection**

- процессы, окна, активное приложение;
- пути файлов + рабочие директории → привязка к проекту;
- Git: ветка, HEAD, dirty-файлы, число untracked;
- терминал: процесс + CWD (через CommandLine);
- браузер: title всегда, URL — opportunistically;
- системные события: старт/конец сессии, сон/пробуждение.

**Engines + Storage**

- Context Engine, Session Engine, Timeline Engine, Snapshot Engine, Stopped Engine, Restore Engine;
- SQLite, privacy-конвейер до записи, схема ниже.

**Adapters**

- VS Code (через `state.vscdb` и `User/History`), Git, Terminal, Chrome (basic).

**UI (WPF, интерфейс на русском)**

- `MainWindow` — «Последняя сессия» (проект, длительность, приложения, файлы, вкладки) + «Статус» + `[ Восстановить сессию ]`;
- `TimelineWindow` — «Хронология»: полная история событий сессии;
- `StoppedWindow` — «Где я остановился?»: проект, файл, коммит, незакоммиченное, терминал, статус;
- `SettingsWindow` — «Настройки»: исключения приложений/директорий, «Не записывать никогда», срок хранения, удаление данных;
- `FirstRunDialog` — «Прошлый контекст не найден → [Создать демо-проект]»;
- `TrayIcon` — фоновая работа, «Завершить сессию», «Восстановить», «Открыть»;
- автозапуск через `HKCU\...\Run` или ярлык в папке Startup (не HKLM — админка не нужна).

**Демо и тесты**

- `Continuum.exe --demo` — синтетическая сессия в отдельной БД (`continuum.demo.db`);
- юнит-тесты чистых функций: определение проекта, правила связей, статус «остановился», фильтр/санитизация, построение RestorePlan.

### Non-goals (после MVP)

- полный Chrome adapter, точный список всех вкладок всех окон;
- Browser Extension;
- Claude Code / Codex / OpenCode adapters;
- точное позиционирование окон при восстановлении;
- скоринг с весами и проценты в UI;
- SDK для сторонних разработчиков;
- AI-объяснения;
- облако, Linux, сложная аналитика.

Цель семестра — не «интеграция со всем», а:

> **Доказать концепцию end-to-end на одном реальном сценарии.**

---

## Схема БД

Миграции — SQL-скриптами вручную через `PRAGMA user_version`. Без EF.

**Правило разделения:** в реляционные таблицы выносится то, по чему выполняются запросы (Timeline, «Где я остановился?», RestorePlan, счётчики в `MainWindow`). Всё остальное — в `summary_json` снапшота. Дублирование одного факта в таблице и в JSON не допускается.

```sql
-- v1

settings        (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at INTEGER NOT NULL)

applications    (id INTEGER PRIMARY KEY, name TEXT NOT NULL, exe_path TEXT,
                 adapter_key TEXT, first_seen INTEGER, last_seen INTEGER)

projects        (id INTEGER PRIMARY KEY, name TEXT NOT NULL,
                 root_path TEXT NOT NULL UNIQUE, git_remote TEXT,
                 first_seen INTEGER, last_seen INTEGER)

sessions        (id INTEGER PRIMARY KEY, started_at INTEGER NOT NULL, ended_at INTEGER,
                 status TEXT NOT NULL,            -- running | ended
                 end_reason TEXT,                 -- idle | user | shutdown | process_exit | crash_recovered
                 idle_threshold_s INTEGER NOT NULL)

-- Timeline. Append-only. Событие пишется на ИЗМЕНЕНИЕ состояния, не по таймеру.
events          (id INTEGER PRIMARY KEY, session_id INTEGER NOT NULL REFERENCES sessions(id),
                 ts INTEGER NOT NULL, kind TEXT NOT NULL,
                 application_id INTEGER REFERENCES applications(id),
                 project_id INTEGER REFERENCES projects(id),
                 title TEXT, path TEXT, url TEXT, meta_json TEXT)
                 -- kind: session_start | session_end | app_focused | app_started | app_exited
                 --       file_changed | file_opened | git_commit | git_branch_switch
                 --       terminal_activity | browser_navigate | system_sleep | system_wake

-- «Файлы за сессию» — event-уровень, не snapshot-уровень.
file_activity   (id INTEGER PRIMARY KEY, session_id INTEGER NOT NULL REFERENCES sessions(id),
                 ts INTEGER NOT NULL, project_id INTEGER REFERENCES projects(id),
                 path TEXT NOT NULL, change_kind TEXT NOT NULL,   -- modified | added | deleted | untracked
                 source TEXT NOT NULL)                            -- git | watcher | recent | vscode_history

snapshots       (id INTEGER PRIMARY KEY, session_id INTEGER NOT NULL REFERENCES sessions(id),
                 ts INTEGER NOT NULL, reason TEXT NOT NULL,       -- timer | user | shutdown | session_end
                 schema_version INTEGER NOT NULL, summary_json TEXT NOT NULL)

git_state       (id INTEGER PRIMARY KEY, snapshot_id INTEGER NOT NULL REFERENCES snapshots(id),
                 project_id INTEGER NOT NULL REFERENCES projects(id),
                 available INTEGER NOT NULL,        -- 0 = git не найден / не репозиторий
                 branch TEXT, head_commit TEXT, head_subject TEXT, head_ts INTEGER,
                 dirty_count INTEGER, dirty_files_json TEXT, untracked_count INTEGER)

terminals       (id INTEGER PRIMARY KEY, snapshot_id INTEGER NOT NULL REFERENCES snapshots(id),
                 available INTEGER NOT NULL, host TEXT NOT NULL,   -- WindowsTerminal | conhost | ...
                 shell TEXT, cwd TEXT, source TEXT NOT NULL)       -- cmdline | peb | unknown

open_files      (id INTEGER PRIMARY KEY, snapshot_id INTEGER NOT NULL REFERENCES snapshots(id),
                 path TEXT NOT NULL, project_id INTEGER REFERENCES projects(id),
                 application_id INTEGER REFERENCES applications(id),
                 line INTEGER,                      -- для code -g path:line
                 last_modified INTEGER, source TEXT NOT NULL)

browser_tabs    (id INTEGER PRIMARY KEY, snapshot_id INTEGER NOT NULL REFERENCES snapshots(id),
                 application_id INTEGER NOT NULL REFERENCES applications(id),
                 url TEXT, title TEXT,
                 url_available INTEGER NOT NULL,    -- 0 = URL недоступен, есть только title
                 is_active INTEGER NOT NULL, source TEXT NOT NULL)  -- window_title | uia | unavailable

restore_plans   (id INTEGER PRIMARY KEY, snapshot_id INTEGER NOT NULL REFERENCES snapshots(id),
                 created_at INTEGER NOT NULL, dry_run INTEGER NOT NULL,
                 status TEXT NOT NULL,              -- pending | success | partial | failed
                 started_at INTEGER, finished_at INTEGER)

restore_steps   (id INTEGER PRIMARY KEY, plan_id INTEGER NOT NULL REFERENCES restore_plans(id),
                 seq INTEGER NOT NULL, strategy TEXT NOT NULL,
                 target TEXT, args_json TEXT,
                 result TEXT,                       -- success | partial | failed | skipped
                 error TEXT, duration_ms INTEGER)

CREATE INDEX ix_events_session_ts   ON events(session_id, ts);
CREATE INDEX ix_events_ts           ON events(ts);
CREATE INDEX ix_fileact_session_ts  ON file_activity(session_id, ts);
CREATE INDEX ix_fileact_path        ON file_activity(path);
CREATE INDEX ix_snapshots_session   ON snapshots(session_id, ts);
CREATE INDEX ix_restore_steps_plan  ON restore_steps(plan_id, seq);
```

Как это используется:

- **Timeline** — `SELECT ... FROM events WHERE session_id = ? ORDER BY ts`, без изменений задним числом;
- **Snapshot** — реляционные таблицы + `summary_json` для быстрого построения RestorePlan;
- **«Где я остановился?»** — последний `snapshot` + `file_activity` + `git_state` + `events` после `head_ts`;
- **RestorePlan** — `open_files`, `browser_tabs`, `terminals`, `applications` → упорядоченные `restore_steps`;
- **счётчики `MainWindow`** — `COUNT(DISTINCT application_id)`, `COUNT(DISTINCT path)` в `file_activity`, `COUNT(*)` в `browser_tabs`.

### Детерминированный алгоритм «Где я остановился?»

```text
last_project = проект последнего записанного события сессии
last_file    = последний путь из file_activity в этом проекте
last_commit  = git log -1 --format=%H%x09%ct%x09%s
after_commit = git status --porcelain --no-optional-locks   (состояние на сейчас)

status:
  "Работа, возможно, не завершена"  если after_commit > 0 ИЛИ последнее событие = правка файла
  "Работа завершена"                если after_commit = 0 И последнее событие = commit / push
  "Статус неясен"                   если git-репозиторий не найден (не ошибка, а другой сценарий)
```

Эта таблица истинности покрывается юнит-тестами на фиксированных входных данных — именно она доказывает тезис «детерминированно, без LLM».

### Границы сессии

```text
START   запуск приложения при отсутствии активной сессии
        возврат ввода после idle-порога (GetLastInputInfo, по умолчанию 15 мин)

END     idle ≥ порога
        Application.OnSessionEnding / WM_QUERYENDSESSION  (~5 с на синхронную запись снапшота)
        явное «Завершить сессию»
        завершение процесса

CRASH-RECOVERY (при старте)
        все сессии с ended_at IS NULL закрываются временем последнего события,
        end_reason = crash_recovered
```

---

## Бюджет ресурсов

Фоновый рекордер обязан быть невидимым. Цели измеряемы и проверяются на защите через Диспетчер задач.

| Метрика | Цель |
| --- | --- |
| CPU в среднем | < 1–2% одного ядра |
| RAM | < 150 МБ |
| Рост БД | < ~10 МБ/сутки |
| Опрос окон | 2–5 с |
| Git-опрос | 15–60 с, только при смене проекта/ветки |
| WMI/CIM-запросы | не чаще раза в 15–30 с |
| Запись в БД | батчем, в одной транзакции |

Правила:

- событие пишется **на изменение**, не на тик; «ничего не изменилось» — это heartbeat, не событие;
- `Process.GetProcesses()` в цикле не используется — вместо него `CreateToolhelp32Snapshot` или кэш с инвалидацией;
- `FileSystemWatcher` — с фильтрами и буфером, никогда не наблюдает `node_modules`, `.git`, `bin`, `obj`, `AppData`.

---

## Порядок доведения до MVP

Вертикальный срез. Chrome и терминал сознательно в конце: они самые хрупкие и меньше всего влияют на главный сценарий.

| # | Шаг | Критерий готовности |
| --- | --- | --- |
| 0 | **Разделение проектов.** `Continuum.Core` (`net10.0`) + `Continuum.App` (`net10.0-windows`), DI-хост, пути `%LOCALAPPDATA%`, `IClock` | Core собирается без `-windows`; из Core недоступны типы WPF и WinAPI |
| 1 | **Domain + SQLite + privacy-конвейер.** Модели, создание БД, `PRAGMA user_version`, миграции, `IPrivacyFilter`/`ISanitizer` **до первой записи** | БД создаётся при первом запуске; ни одна запись не минует конвейер (проверяется тестом) |
| 2 | **Process/Window collector + tray.** Список процессов, `EnumWindows`, активное окно, заголовки, события-на-изменение | Смена активного окна даёт ровно одно событие; tray работает, окно не мешает |
| 3 | **Project + Git + file_activity.** Определение git root, `GitClient` (`--no-optional-locks`, `GIT_TERMINAL_PROMPT=0`, таймаут, поиск git.exe), dirty-файлы | Проект определяется по пути файла и по CWD; при отсутствии git — `available = 0`, без исключений |
| 4 | **Session → Timeline → Snapshot.** Границы сессии, append событий, снапшот по таймеру и при выходе, crash-recovery | После kill процесса висячая сессия закрывается при следующем старте; снапшот пишется при `OnSessionEnding` |
| 5 | **Stopped Engine.** Реализация таблицы истинности | Юнит-тесты на всех ветках: dirty / clean+commit / нет git |
| 6 | **RestorePlan.** Resolver путей приложений (`App Paths` → Uninstall → известные → PATH), порядок шагов (хост → окно → файлы/URL), dry-run, `success/partial/failed` | Падение одного шага не отменяет остальные; dry-run ничего не запускает; повтор не плодит дубликаты |
| 7 | **VS Code adapter.** `state.vscdb`, `User/History/*/entries.json`, `workspaceStorage/*/workspace.json`; восстановление через `code -g path:line` | Открытые файлы и последняя строка читаются из фактов, а не угадываются по заголовкам |
| 8 | **`--demo` + юнит-тесты.** Seed-режим с отдельной БД, генератор демо-проекта, тесты чистых функций | Полный цикл (`Хронология` → `Где я остановился?` → `RestorePlan`) показывается на машине без VS Code и git |
| 9 | **SettingsWindow.** Исключения приложений/директорий, «Не записывать никогда», retention, полное удаление (включая `-wal`/`-shm`) | Исключение действует немедленно, без перезапуска; удаление стирает всё |
| 10 | **Chrome + Terminal.** Title всегда; URL через UIA opportunistically с флагом `url_available`; CWD терминала через CommandLine (PEB — опционально) | При недоступном UIA интерфейс показывает «URL недоступен», а не пустоту; `wt -d <path>` даёт CWD |

Контекстные связи в MVP — **только жёсткие правила** (равенство git root, равенство каталога, принадлежность снапшоту). Веса и проценты — после рабочего среза и замеров; в UI проценты не показываются.

---

## Демо-сценарий (защита, без перезагрузки)

```text
1.  Запустить Continuum.exe на учебном ПК (обычный пользователь)
2.  Нажать [Создать демо-проект]
3.  Открыть сгенерированный проект в VS Code
4.  Изменить 1–2 файла
5.  Выполнить команды в терминале из папки проекта
6.  Сделать git commit
7.  Открыть 1–2 URL документации
8.  Нажать «Завершить сессию» / закрыть приложения
9.  Показать «Хронологию» и «Где я остановился?»
10. Нажать [Восстановить сессию] → VS Code + проект + файлы (на нужной строке) + терминал восстановлены
11. Показать результат каждого шага: ✓ / ⚠ / ✗
```

Перезагрузка Windows — опциональная вторая демонстрация (`Сохранить → перезагрузка → Восстановить`), не обязательная.

Demo workspace:

```text
DemoProject/
├── README.md
├── src/
│   ├── Program.cs
│   └── Main.cs
└── project.config
```

### Страховка: `--demo`

`Continuum.exe --demo` заполняет отдельную БД (`continuum.demo.db`) синтетической, но реалистичной сессией: проект, 40–60 событий, git-состояние, снапшот. Позволяет показать `Хронологию`, `Где я остановился?` и `RestorePlan` **независимо от того, что установлено на учебном ПК**.

Это единственная мера, снимающая риск «демо умерло на чужой машине» — а он для курсовой фатальнее любого недостающего адаптера.

### Чек-лист рисков на чужом ПК

Проверить до защиты, на каждый пункт есть запасной путь:

| Риск | Запасной путь |
| --- | --- |
| Нет VS Code | `--demo`; восстановление `OpenFile` в ассоциированный редактор |
| git.exe не в PATH | Поиск через `App Paths`; иначе статус «Статус неясен» (`available = 0`) |
| Нет Chrome | `browser_tabs` пуст, шаг плана `skipped` с причиной |
| UIA заблокирован политикой | `url_available = 0`, в UI «URL недоступен» |
| Store-версия Terminal в AppContainer, PEB не читается | Fallback на CommandLine через CIM |
| Антивирус/AppLocker режет распаковку в temp | Folder-publish вместо single-file (см. «Сборка») |
| Нет интернета | Не требуется: сетевых вызовов нет |

---

## Запуск и сборка

Требования для разработки: .NET 10 SDK.

```powershell
# запуск из исходников
dotnet run --project src/Continuum.App

# юнит-тесты
dotnet test

# ДЕМО-СБОРКА (рекомендуется): папка, копирование без установки
dotnet publish src/Continuum.App -c Release -r win-x64 --self-contained true -o publish

# single-file — только если temp-распаковка гарантированно разрешена
dotnet publish src/Continuum.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

**Почему для защиты folder-publish.** Single-file WPF распаковывает нативные библиотеки (в том числе `e_sqlite3.dll`) во временную папку: нужен `IncludeNativeLibrariesForSelfExtract=true`, иначе на чистой машине будет «unable to load e_sqlite3» прямо во время демонстрации. Распаковку в temp иногда режет AppLocker или антивирус в учебных классах, а первый запуск медленный. Folder-publish сохраняет сценарий «скопировал папку → запустил» и убирает все три риска.

Решение — классический `Continuum.sln` (выбрано на шаге 0): открывается любым Visual Studio и любым SDK, в отличие от `.slnx`.

После первого запуска данные здесь:

```text
%LOCALAPPDATA%\Continuum\continuum.db        (+ continuum.db-wal, continuum.db-shm)
%LOCALAPPDATA%\Continuum\continuum.demo.db   (режим --demo)
```

Сброс состояния:

```powershell
Remove-Item "$env:LOCALAPPDATA\Continuum\continuum.db*"
```

---

## Roadmap после MVP

```text
Core (стабилен) → Adapters растут → Community пишет continuum-adapter-*
```

Кандидаты: полный Chrome adapter, Claude Code / OpenCode adapters, Browser Extension, точные позиции окон, скоринг с весами, SDK, AI-слой (`Structured Context → естественный язык`), Avalonia / Web UI.

Расширение функциональности выполняется через адаптеры **без изменения ядра**.

---

## Prior art

Вопрос «это же уже есть» неизбежен, поэтому сравнение фиксируется сразу.

| Решение | Что делает | Отличие Continuum |
| --- | --- | --- |
| **Rewind / Limitless** | Скриншоты всего экрана + OCR, облако, поиск по записям | Без скриншотов и облака, zero-admin; восстанавливает структуру (проект → файлы → git), а не картинки |
| **ActivityWatch** (open source) | AFK-детект, активные окна, длительности, локально | Ближайший аналог по сбору. Но там **аналитика времени**, здесь реконструкция и восстановление контекста: RestorePlan, git-состояние, «где я остановился» |
| **Windows Timeline** | Карточки активности по дням | Удалён в Windows 11; показывает активность, не восстанавливает окружение; привязан к учётке Microsoft и облаку |
| **Windows / macOS «восстановить окна»** | Повторное открытие окон приложений | Отвечает «что было открыто»; не знает про ветки, dirty-файлы, терминалы и строку остановки |
| **JetBrains / VS Code Local History** | История правок одного файла внутри одной IDE | Внутри одного инструмента и одного файла; Continuum связывает **между приложениями** и на уровне проекта |
| **Менеджеры сессий браузера** | Восстановление вкладок | Только браузер, только вкладки |

Новизна не в сборе данных (собирают многие), а в **связывании** их в проект и в **восстановлении** с честным отчётом о результате каждого шага.

Дифференциатор одной фразой:

> **Проектно-центрированная детерминированная реконструкция рабочего контекста между приложениями + RestorePlan с результатом каждого шага, от обычного пользователя и полностью локально.**

---

## Статус

Сейчас: **шаг 0 выполнен** — решение разделено на `src/Continuum.Core` (`net10.0`, без WinAPI и WPF; проверяется архитектурным тестом), `src/Continuum.App` (`net10.0-windows`, WPF, на выходе `Continuum.exe`) и `tests/Continuum.Tests` (xunit, 4 теста зелёные). DI-композиция (`CompositionRoot`), пути `%LOCALAPPDATA%\Continuum` (`AppPaths`), `IClock`/`SystemClock` и контракты privacy-конвейера (`IPrivacyFilter`, `ISanitizer`, `IExclusionSet`, `Observable`) на месте. `MainWindow` запускается через DI, логики в нём пока нет.

Документация в согласованном состоянии: `readme.md` — источник истины по скоупу, нормативные спецификации — в `docs/specs/`.

Следующий шаг — **шаг 1**: доменные модели, SQLite в `%LOCALAPPDATA%\Continuum\`, `PRAGMA user_version`, первая миграция, реализация privacy-конвейера до первой записи в БД.

---

## Лицензия

MIT. Текст — в файле [`LICENSE`](./LICENSE).
