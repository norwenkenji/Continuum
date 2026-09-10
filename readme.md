# Continuum

### Personal Work Context Engine

**Continuum - локальная система сохранения, анализа и восстановления рабочего контекста.**

> Компьютер умеет восстанавливать открытые окна. Continuum отвечает на другой вопрос: **«Чем человек занимался и на каком этапе остановился?»**

---

## Проблема

Типичная рабочая сессия разработчика выглядит так:

```text
VS Code
├── Project A
├── Project B
└── несколько файлов

Windows Terminal
├── PowerShell
├── Git
└── CLI-инструменты (Claude Code, OpenCode)

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

Continuum строит структурированную модель рабочего контекста:

```text
Collectors → Context Engine → Session / Timeline / Snapshot → Restore Engine → RestorePlan
```

Три базовые сущности:

- **Context** - текущее состояние окружения (приложения, окна, файлы, URL, терминалы, проекты).
- **Session** - период непрерывной работы. Отвечает: *что происходило?*
- **Snapshot** - точка состояния. Отвечает: *как выглядел контекст в конкретный момент?*

Поверх них:

- **Timeline** - история событий сессии.
- **«Где я остановился?»** - последний проект, последний файл, последний коммит, изменения после коммита, последний терминал, вероятный статус (`Работа, возможно, не завершена / Завершена`).
- **RestorePlan** - пошаговый план восстановления с результатом каждой операции (`success / partial / failed`).

В MVP всё это **детерминированно, без LLM**.

---

## Принципы

Правила зафиксированы и не обсуждаются в рамках MVP:

1. **Обычный пользователь.** Работа без админских прав. Нет UAC, служб, драйверов, HKLM, системных каталогов.
2. **Portable / zero-setup.** Сценарий: скопировал папку → запустил `Continuum.exe` → работает. Ничего заранее не настраивается.
3. **User-isolated.** Данные лежат в пользовательском пространстве, например `%LOCALAPPDATA%\Continuum\`. Профили Windows не смешиваются.
4. **Local-first.** Хранилище - SQLite. По умолчанию ничего не уходит в облако, интернет не нужен.
5. **UI тонкий.** WPF - только оболочка над `Application / Core`. Бизнес-логики в окнах нет.
6. **Интерфейс на русском.** Все видимые строки, кнопки и статусы - русские. Английскими остаются только имена классов и технические термины в коде.

Стек MVP:

```text
Windows + .NET 10 (net10.0-windows) + WPF + SQLite (Microsoft.Data.Sqlite)
```

---

## Архитектура

```text
Continuum
├── Core
│   ├── Domain (Context, Session, Snapshot, Activity, Application, Project, RestorePlan)
│   └── Interfaces
├── Application
│   ├── ContextService
│   ├── SessionService
│   ├── TimelineService
│   ├── SnapshotService
│   ├── RestoreService
│   └── ProjectService
├── Infrastructure
│   ├── Database (SQLite)
│   ├── Windows (ProcessCollector, WindowCollector, SystemEvents)
│   ├── Files / Git / Browser (basic) / Terminal (basic)
├── Adapters
│   ├── VSCode / Chrome (basic) / Git / Terminal
│   └── ... (потом, без изменения Core)
└── Presentation (WPF)
```

Два ключевых решения против архитектурного спагетти:

**Adapter API** вместо `if Chrome / if VSCode / ...`:

```text
Detect() → CaptureState() → RestoreState()
```

**RestoreStrategy** вместо знания о каждой программе в Core:

```text
StartProcess / OpenFile / OpenURL / ExecuteCommand / BrowserExtension / API / CustomAdapter
```

Пример:

```text
README.md        → OpenFile
GitHub URL       → OpenURL
Windows Terminal → StartProcess (с рабочей директорией)
Сложное приложение → CustomAdapter
```

Неизвестное приложение тоже сохраняется и восстанавливается на базовом уровне: exe, рабочая директория, окно (позиция/размер), связанные файлы.

---

## Данные и приватность

Continuum потенциально видит чувствительное: URL, пути файлов, команды, AI-сессии. Поэтому не так:

```text
захват всего → SQLite
```

А так:

```text
Наблюдаемый объект → Фильтр → Очистка → Сохранение
```

В MVP:

- Исключения приложений и директорий;
- `Не записывать никогда`: менеджеры паролей, банковские сайты, приватные окна;
- Редактирование секретов в командах;
- настройки удержания + полное удаление данных.

Принцип: **Capture context ≠ capture everything.**

---

## MVP Scope

### Must

Platform:

- Windows, обычный пользователь, без админки.

Collection:

- процессы, окна, активное приложение;
- файлы + рабочие директории → привязка к проекту;
- Git: ветка, dirty-файлы, последний коммит;
- терминал: базовый контекст (процесс, CWD);
- браузер: базовый URL активной вкладки;
- системные события (старт/конец сессии, сон/пробуждение).

Engines + Storage:

- Context Engine, Session Engine, Timeline Engine, Snapshot Engine, Restore Engine;
- SQLite, схема ниже.

Adapters:

- VS Code (минимальный), Git, Terminal, Chrome (basic).

UI (WPF, несколько окон, интерфейс на русском):

- `MainWindow`: `Последняя сессия` (проект, длительность, приложения, файлы, табы) + `Статус` + кнопка `[ Восстановить сессию ]`;
- `TimelineWindow`: `Хронология` - полная история событий сессии;
- `StoppedWindow`: `Где я остановился?` - последний проект, файл, коммит, незакоммиченное после коммита, терминал;
- `SettingsWindow`: `Настройки` - исключения приложений/директорий, `Не записывать никогда`, срок хранения, удаление данных;
- `FirstRunDialog`: `Прошлый контекст не найден → [Создать демо-проект]`.

### Non-goals (после MVP)

- Полный Chrome adapter, точные табы всех окон;
- Claude Code / Codex / OpenCode adapters;
- Browser Extension;
- точное позиционирование окон;
- SDK для сторонних разработчиков;
- AI-объяснения;
- облако, Linux, сложная аналитика.

Цель семестра - не «интеграция со всем», а:

> **Доказать концепцию end-to-end на одном реальном сценарии.**

---

## Схема БД (драфт)

Миграции - SQL-скриптами руками через `PRAGMA user_version`. Без EF.

```sql
sessions   (id, started_at, ended_at, status)
events     (id, session_id, ts, type, app, title, path, url, project, meta_json)
snapshots  (id, session_id, ts, summary_json)
projects   (id, name, root_path, git_remote, last_seen)
open_files (snapshot_id, path, project, app, last_modified)
git_state  (snapshot_id, project, branch, last_commit, dirty_count, dirty_files_json)
terminals  (snapshot_id, host, cwd, shell, last_command_redacted)
```

`Timeline` - append-only чтение из `events`.
`Snapshot` - JSON-blob + мета для быстрого `RestorePlan`.
`Где я остановился?` - запрос поверх последнего `snapshot` + `events` после последнего коммита.

---

## Порядок доведения до MVP

Вертикальный срез, по порядку:

1. **Domain + SQLite init.** Модели `Session/Event/Snapshot/Project`, создание БД в `%LOCALAPPDATA%\Continuum\`, `user_version`.
2. **Process/Window collector.** Список процессов, окна (`EnumWindows`), активное окно, заголовки.
3. **File + Git → Project.** CWD процессов, определение корня проекта / git-репозитория (`git rev-parse --show-toplevel`), `branch/dirty/last commit`.
4. **Session → Timeline → Snapshot.** Старт/стоп сессии, append событий, сохранение снапшота по таймеру и при выходе.
5. **Restore minimal.** `OpenFile / StartProcess / OpenURL`, `RestorePlan` с `success/partial/failed`, отказоустойчивость к одной упавшей операции.
6. **VS Code adapter.** Детект запущенного VS Code, workspace, открытые файлы (эвристика по cmdline + заголовкам).
7. **UI + демо.** `MainWindow` (`Последняя сессия` + `Восстановить`), `TimelineWindow` (`Хронология`), `StoppedWindow` (`Где я остановился?`), `SettingsWindow`, генератор демо-проекта.

Контекстный скоринг на старте - простые правила (`same git root`, `same directory`). Веса и проценты - только после рабочего среза.

---

## Демо-сценарий (защита, без перезагрузки)

```text
1.  Запустить Continuum.exe на учебном ПК (обычный пользователь)
2.  Нажать [Создать демо-проект]
3.  Открыть сгенерированный проект в VS Code
4.  Изменить 1-2 файла
5.  Выполнить команды в терминале из папки проекта
6.  Сделать git commit 
7.  Открыть 1-2 URL документации
8.  Нажать Завершить сессию / закрыть приложения
9.  Показать Хронологию + Где я остановился?
10. Нажать [Восстановить сессию] → VS Code + проект + файлы + терминал восстановлены
```

Перезагрузка Windows - опциональная вторая демонстрация (`Сохранить → перезагрузка → Восстановить`), не обязательная.

Demo workspace:

```text
DemoProject/
├── README.md
├── src/
│   ├── Program.cs
│   └── Main.cs
└── project.config
```

---

## Запуск

Требования для разработки: .NET 10 SDK.

```powershell
# запуск из исходников
dotnet run

# сборка portable для демо 
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

После первого запуска данные здесь:

```text
%LOCALAPPDATA%\Continuum\continuum.db
```

Сброс состояния:

```powershell
Remove-Item "$env:LOCALAPPDATA\Continuum\continuum.db"
```

---

## Roadmap после MVP

```text
Core (стабилен) → Adapters растут → Community пишет continuum-adapter-*
```

Кандидаты: полный Chrome adapter, Claude Code / OpenCode adapters, Browser Extension, точные позиции окон, SDK, AI-слой (`Structured Context → естественный язык`), Avalonia / Web UI.

---

## Статус

Сейчас: голый WPF-шаблон (`MainWindow` пуст), логики нет. Следующий шаг - пункт 1 из раздела «Порядок доведения»: Domain + SQLite init.

---

## Лицензия

MIT. Текст - в файле [`LICENSE`](./LICENSE).
