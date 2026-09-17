# Спецификация: Источники данных

- Статус: **нормативный документ**. Расхождение между этим файлом и реализацией — дефект реализации.
- Область: каждый наблюдаемый источник — способ получения, права, надёжность, поведение при недоступности.
- Смежные документы: [`readme.md`](../../readme.md) (скоуп, схема БД), [`privacy-pipeline.md`](privacy-pipeline.md) (что разрешено записывать).

---

## 0. Общие правила

### 0.1 Среда

Все способы рассчитаны на **обычного пользователя без административных прав**. Если источник требует повышения — он не используется, вместо него fallback или `unavailable`.

### 0.2 Класс надёжности

Каждому источнику присвоен класс. Он определяет обработку отказа:

| Класс | Значение | Поведение при отказе |
| --- | --- | --- |
| **A** | Стабильно, без внешних зависимостей | Отказ = баг, исключение логируется |
| **B** | Зависит от внешнего инструмента или состояния | Флаг `available = 0`, шаг плана `skipped`, UI показывает «недоступно» |
| **C** | Best-effort, часто недоступен | Тихая деградация, флаг `*_available = 0`, без исключений в логе пользователя |

Никакой источник класса B или C не имеет права ронять приложение или блокировать сессию.

### 0.3 Таймауты и частота

| Источник | Частота | Таймаут |
| --- | --- | --- |
| Окна / активное окно | 2–5 с | — |
| Процессы (`CreateToolhelp32Snapshot`) | 5–15 с | — |
| CIM `Win32_Process` | 15–30 с | 3 с |
| `git status` / `git log` | 15–60 с, только при смене проекта или ветки | 5 с |
| Чтение `state.vscdb` / `entries.json` | 30–60 с | 2 с |
| UIA (адресная строка Chrome) | по смене активного окна, не чаще 1 раза в 2 с | 1 с |
| `FileSystemWatcher` | событийный | — |
| `Recent\*.lnk` | 5 мин | 2 с |

Общий бюджет — в [`readme.md`](../../readme.md), раздел «Бюджет ресурсов»: CPU < 1–2%, RAM < 150 МБ, рост БД < ~10 МБ/сутки.

### 0.4 Событие на изменение, не на тик

Опрос — это способ **заметить** изменение. В БД пишется событие только когда состояние изменилось: сменилось активное окно, проект, ветка, сохранён файл. «Ничего не изменилось» — heartbeat, не событие.

---

## 1. Процессы

| | |
| --- | --- |
| **Класс** | A |
| **Способ (основной)** | `CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS)` через P/Invoke |
| **Способ (fallback)** | `Process.GetProcesses()` — не в цикле, только при разовых операциях |
| **Что получаем** | PID, имя образа, путь exe, родительский PID |
| **Права** | Процессы своего пользователя — полные данные. Чужие процессы: `OpenProcess` вернёт `ACCESS_DENIED` для пути; имя процесса доступно через snapshot |
| **Запись в БД** | `applications` (имя, exe_path, first/last_seen) |

**Почему не `Process.GetProcesses()` в цикле:** каждая итерация аллоцирует массив объектов `Process`, каждый из которых держит неуправляемый дескриптор. При опросе раз в 5 секунд это постоянный GC-мусор и утечка дескрипторов при забытом `Dispose()`. `CreateToolhelp32Snapshot` — одна нативная структура, читаемая в цикле.

**Путь exe без админки:** `QueryFullProcessImageName` с `PROCESS_QUERY_LIMITED_INFORMATION` работает для процессов своего пользователя и для большинства системных. Для elevated-процессов другого уровня целостности вернёт отказ — тогда `exe_path = NULL`, класс A деградирует в B для этой записи.

**Родительский PID** нужен для терминалов (раздел 6): у `CreateToolhelp32Snapshot` он есть прямо в `PROCESSENTRY32.th32ParentProcessID`.

---

## 2. Окна

| | |
| --- | --- |
| **Класс** | A |
| **Способ** | `EnumWindows` + `GetWindowThreadProcessId` + `GetWindowText` + `IsWindowVisible` |
| **Активное окно** | `GetForegroundWindow()` |
| **Позиция/размер** | `GetWindowRect` (или `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` для точности) |
| **Права** | Не требуются. Заголовки окон своего пользователя читаются всегда |
| **Запись в БД** | `events` (kind = `app_focused`, title), `open_files` (позиция окна — после MVP) |

Фильтр мусорных окон:

```text
IsWindowVisible(hwnd) == true
GetWindowTextLength(hwnd) > 0
нет стиля WS_EX_TOOLWINDOW
не является дочерним (GetWindow(GW_OWNER) == null)
класс окна не в списке служебных (Progman, WorkerW, Shell_TrayWnd, ...)
```

Событие `app_focused` пишется **только при смене** `hwnd` или заголовка активного окна, с дедупликацией по (pid, title).

**Ограничение.** Заголовок окна — это то, что приложение захотело показать. Для Chrome это title страницы, не URL (раздел 5). Для VS Code — «файл — папка — Visual Studio Code» (раздел 7). Заголовок нельзя считать структурированным источником; он парсится эвристически и помечается `source`.

---

## 3. Файлы и активность с файлами

Число «изменено файлов» не возникает из наблюдения за процессами. Нужен явно названный источник.

| Источник | Класс | Что даёт | Когда использовать |
| --- | --- | --- | --- |
| `git status --porcelain --no-optional-locks` | B | Точный список dirty-файлов репозитория + тип изменения | **Основной** для проектов с git |
| `FileSystemWatcher` на корне проекта | A | Факт изменения/создания/удаления файла | Только для обнаруженных проектов |
| `%APPDATA%\Microsoft\Windows\Recent\*.lnk` | B | Недавно открытые документы с временем | Работает и для не-кода: Office, PDF, изображения |
| `%APPDATA%\Code\User\History\*\entries.json` | B | Точные пути и timestamp каждого сохранения в VS Code | Раздел 7 |

**Запись в БД:** `file_activity(session_id, ts, project_id, path, change_kind, source)`. Поле `source` обязательно — оно показывает, откуда факт, и позволяет не доверять слабым источникам.

### 3.1 `FileSystemWatcher`

Обязательные настройки:

```text
IncludeSubdirectories = true, но с исключениями:
    node_modules, .git, bin, obj, .vs, AppData, packages,
    target, dist, build, __pycache__, .venv, vendor
NotifyFilter = LastWrite | FileName | Size
InternalBufferSize = 64 КБ (больше для крупных проектов)
Дедупликация: одно и то же (path, change_kind) не чаще раза в N секунд
```

Редакторы часто пишут файл через временный файл с переименованием — это даёт пару `Created`+`Renamed`. Дедупликация по пути обязательна, иначе одна сохранённая страница даст 3–5 событий.

### 3.2 `Recent\*.lnk`

Чтение через `IShellLink` (COM, `IPersistFile.Load` + `GetPath`). Даёт путь цели и время. Имя `.lnk`-файла содержит timestamp в некоторых версиях Windows — полагаться на это не следует, использовать `File.GetLastWriteTime`.

Ограничение: показывает **открытые** документы, не изменённые; и не покрывает всё (часть приложений не создаёт ярлыки). Поэтому `change_kind` для этого источника — `opened`, не `modified`.

---

## 4. Git

| | |
| --- | --- |
| **Класс** | B |
| **Обёртка** | `GitClient` — единственный компонент, запускающий `git.exe` |
| **Права** | Не требуются. Работает в репозиториях, доступных пользователю |
| **Запись в БД** | `git_state(available, branch, head_commit, head_subject, head_ts, dirty_count, dirty_files_json, untracked_count)` |

### 4.1 Поиск git.exe

`git` может отсутствовать в PATH — иначе всё git-состояние молча пропадёт именно на чужом ПК. Порядок поиска:

```text
1. PATH (where git)
2. HKLM\SOFTWARE\GitForWindows → InstallPath   (чтение HKLM админки не требует)
3. HKCU\SOFTWARE\GitForWindows → InstallPath
4. %LOCALAPPDATA%\Programs\Git\cmd\git.exe
5. %LOCALAPPDATA%\GitHubDesktop\app-*\resources\app\git\cmd\git.exe
6. %ProgramFiles%\Git\cmd\git.exe
```

Результат кэшируется на время жизни процесса. Не найден → `available = 0`, UI показывает «Git недоступен», статус «Статус неясен».

### 4.2 Разрешённые команды

```text
git rev-parse --show-toplevel                      корень репозитория
git rev-parse --abbrev-ref HEAD                    ветка
git log -1 --format=%H%x09%ct%x09%s                последний коммит
git status --porcelain --no-optional-locks         dirty-файлы
```

**Запрещено:**

```text
git fetch / pull / push / ls-remote / clone   ← сетевые, нарушают local-first
git commit / add / reset / checkout / stash   ← изменяют репозиторий пользователя
git config --global ...                       ← глобальные побочные эффекты
любая команда без --no-optional-locks, если она читает индекс
```

`--no-optional-locks` критичен: без него `git status` обновляет индекс и может конфликтовать с git-операциями самого пользователя (в частности, ломать `git status` в его терминале в момент вызова).

### 4.3 Переменные окружения вызова

```text
GIT_TERMINAL_PROMPT = 0        запрет интерактивных запросов учётных данных
GIT_OPTIONAL_LOCKS  = 0        страховка на уровне окружения
GCM_INTERACTIVE     = never    Git Credential Manager не показывает UI
credential.helper =            (пустое значение через -c) отключает хелпер
LC_ALL = C                     стабильный язык сообщений
```

Без `GIT_TERMINAL_PROMPT=0` git в репозитории с недоступным remote может **показать окно ввода пароля** из фонового процесса. Это худший возможный побочный эффект для невидимого рекордера.

Каждый вызов: `CreateNoWindow = true`, `UseShellExecute = false`, таймаут 5 с с `Kill(entireProcessTree: true)`, вывод читается асинхронно (иначе возможна взаимная блокировка на полном буфере).

### 4.4 Определение проекта

Приоритет (по убыванию достоверности):

```text
1. git root: подъём вверх от пути файла / CWD до каталога с .git
             (или git rev-parse --show-toplevel)
2. workspace VS Code (из state.vscdb)
3. CWD терминального процесса
4. общий предок затронутых файлов                     (слабейший сигнал)
```

Идентификация — по нормализованному `root_path` (полный путь, регистронезависимо, без завершающего разделителя, с разрешением `..` и subst). `git_remote` сохраняется как дополнительный признак, но первичным ключом не является: папку часто перемещают, remote — нет.

Принятые ограничения MVP:

- вложенные репозитории и git worktree считаются одним проектом по **внешнему** git root;
- перенос папки создаёт новый проект, старый остаётся в истории;
- bare-репозитории не считаются проектом.

---

## 5. Chrome и браузер

Самый хрупкий источник в системе. Требование реализовывать его последним (шаг 10).

### 5.1 Title — всегда

| | |
| --- | --- |
| **Класс** | A |
| **Способ** | `EnumWindows` → `GetWindowText` (раздел 2) |
| **Что получаем** | Title активной вкладки |
| **Запись** | `browser_tabs(title, url = NULL, url_available = 0, source = 'window_title')` |

Формат заголовка Chrome: `<title страницы> - Google Chrome`. Профиль добавляется при не-основном профиле: `<title> - <ProfileName> - Google Chrome`. Суффикс отсекается надёжно; извлечение имени профиля — best-effort.

### 5.2 URL — только через UI Automation

| | |
| --- | --- |
| **Класс** | C |
| **Способ** | UIA: `IUIAutomation.ElementFromHandle(hwnd)` → поиск элемента адресной строки → `ValuePattern.Value` |
| **Что получаем** | URL активной вкладки одного окна |
| **Запись** | `browser_tabs(url, url_available = 1, source = 'uia')` |

**Почему это best-effort.** Chromium строит accessibility-дерево **только по запросу assistive-технологии**. Первый запрос включает дерево асинхронно:

- первые обращения возвращают пустой результат или таймаут;
- включение accessibility имеет наблюдаемую стоимость для браузера (это не «бесплатно»);
- при старте Chrome или смене окна возможна гонка — значение ещё не готово.

Правила реализации:

```text
таймаут на чтение = 1 с, при превышении → url_available = 0
не более одного обращения к UIA на окно за 2 с
кэш результата по (hwnd, title): если title не сменился, URL не перечитывается
любое исключение COM/UIA → url_available = 0, без падения
```

### 5.3 Что **не** используется и почему

| Способ | Причина отказа |
| --- | --- |
| Копия `Chrome\User Data\Default\History` (SQLite) | Файл залочен; читает **историю**, а не текущее состояние; нет привязки к окну и вкладке; копирование через `FileShare.ReadWrite` нестабильно |
| Чтение `Current Session` / `Current Tabs` (SNSS) | Формат закрыт и меняется между версиями Chrome; парсер придётся сопровождать |
| Отладочный протокол (`--remote-debugging-port`) | Требует перезапуска браузера с флагом — нарушает zero-setup и вмешивается в работу пользователя |
| Browser Extension | Non-goal MVP, справедливо |

### 5.4 Следствия для приватности

- точный URL получен не всегда → правила `BY_HOST` применяются только при `url_available = 1`;
- **incognito-окно надёжно не детектируется** ни по title, ни по процессу;
- точный список всех вкладок всех окон — **non-goal**.

Гарантия не-записи достигается исключением приложения целиком. Подробности — [`privacy-pipeline.md`](privacy-pipeline.md), раздел 8.

### 5.5 Другие браузеры

Edge — Chromium, те же механизмы (title `- Microsoft Edge`). Firefox — заголовок даёт title, URL через UIA ненадёжен (другая структура accessibility). В MVP поддерживается Chrome/Edge по title; Firefox — только title.

### 5.6 Восстановление вкладок

`OpenURL` через `StartProcess` с URL-аргументом:

```text
chrome.exe <url1> <url2> ...       в текущее окно, если запущено
```

Если Chrome не запущен — запускается с URL-аргументами. Повторное восстановление плодит дубликаты вкладок, поэтому RestorePlan проверяет уже запущенные процессы и помечает шаг `partial`, если состояние несовместимо (свойство идемпотентности в `readme.md`).

---

## 6. Терминалы

| | |
| --- | --- |
| **Класс** | B (CIM) / C (PEB) |
| **Запись** | `terminals(available, host, shell, cwd, source)` |

### 6.1 Базовый путь: CommandLine через CIM

```csharp
// WMI/CIM, не чаще раза в 15–30 с
SELECT ProcessId, ParentProcessId, Name, CommandLine, ExecutablePath
FROM Win32_Process
WHERE Name LIKE '%.exe'
```

Из CommandLine извлекаются:

```text
wt.exe -d C:\Projects\Foo            → cwd из ключа -d
powershell.exe -NoExit -Command "cd 'C:\Projects\Foo'"   → cwd из cd/Set-Location
cmd.exe /k cd /d C:\Projects\Foo     → cwd из аргумента
code .                               → путь из аргумента
git status                           → команда как terminal_activity (после санитизации)
```

CIM работает для процессов своего пользователя без повышения. Чужие/повышенные процессы — `CommandLine` может прийти `NULL`: это `source = 'unknown'`, а не ошибка.

### 6.2 Точный путь: PEB (Tier 2, опционально)

`NtQueryInformationProcess(ProcessBasicInformation)` → `PEB` → `RTL_USER_PROCESS_PARAMETERS.CurrentDirectory.DosPath`.

- значение **обновляется живьём** при `SetCurrentDirectory` — это единственный способ получить CWD после того, как пользователь перешёл в другую папку;
- админских прав для процесса **своего** пользователя не требуется;
- нужны `PROCESS_QUERY_INFORMATION | PROCESS_VM_READ`.

Ограничения:

```text
Windows Terminal: один WindowsTerminal.exe + дочерние шеллы на таб.
  Обход детей обязателен; OpenConsole.exe — это псевдоконсоль, не шелл, его CWD бесполезен.

Store-версия Windows Terminal работает в AppContainer:
  OpenProcess(PROCESS_VM_READ) может вернуть ACCESS_DENIED.
  → fallback на CommandLine (6.1), source = 'cmdline' или 'unknown'

32/64-битное рассогласование: чтение PEB 64-битного процесса из 32-битного невозможно.
  Continuum собирается x64 — риск снят, но проверка обязательна.
```

Поле `source` в `terminals` показывает, откуда взят `cwd`: `peb` (точный), `cmdline` (из аргументов запуска, мог устареть), `unknown`. UI обязан это различать: «терминал запущен в X» и «терминал сейчас в X» — разные утверждения.

### 6.3 Команды в терминале

Содержимое консоли **не читается** (это был бы перехват ввода — запрещено, см. `readme.md` «Чего Continuum не делает»). Доступны только:

- команда из CommandLine процесса (однократно, при запуске);
- факт активности: смена заголовка окна терминала (многие шеллы пишут туда текущую команду или директорию).

`last_command_redacted` в схеме проходит санитизацию в обязательном порядке (раздел 5.1 `privacy-pipeline.md`).

---

## 7. VS Code

Адаптер читает **факты из локальных хранилищ**, а не угадывает по заголовкам.

| | |
| --- | --- |
| **Класс** | B |
| **Запись** | `open_files(path, line, source = 'vscode_history' \| 'vscode_state' \| 'window_title')` |

### 7.1 Источники

```text
%APPDATA%\Code\User\globalStorage\state.vscdb          (SQLite)
    history.recentlyOpenedPathsList   → недавние папки и файлы workspace
    backupWorkspaces                  → открытые workspace на момент закрытия
    состояние окон

%APPDATA%\Code\User\History\<hash>\entries.json
    точные пути файлов и timestamp КАЖДОГО сохранения

%APPDATA%\Code\User\workspaceStorage\<hash>\workspace.json
    hash → папка workspace (связка хранилища с проектом)
```

`entries.json` — самый ценный источник: он даёт **факт и время сохранения** конкретного файла, без наблюдения за процессом и без `FileSystemWatcher`.

### 7.2 Правила доступа

```text
state.vscdb открывается в режиме read-only
  (ConnectionString: "Mode=ReadOnly"; VS Code держит файл занятым)
копия файла во временную папку, если read-only не сработал
чтение не чаще раза в 30–60 с
любое исключение → fallback на window_title
```

**Категорически запрещено** писать в `state.vscdb` или в `User\History`. VS Code держит их открытыми; вмешательство повредит состояние редактора пользователя.

Для VS Code Insiders / VSCodium / Cursor пути другие (`Code - Insiders`, `VSCodium`, `Cursor`) — адаптер принимает список корней, а не один хардкод.

### 7.3 Разбор заголовка окна (fallback)

Формат: `<file> — <folder> — Visual Studio Code` (в старых версиях разделитель `-`).

```text
"Program.cs — DemoProject — Visual Studio Code"
   → file = Program.cs, folder = DemoProject

"Program.cs — Visual Studio Code"
   → file = Program.cs, folder = unknown (проект не определяется)
```

Разделитель — длинное тире `—` (U+2014), а не дефис. Имена файлов и папок сами могут содержать дефисы, поэтому разбор по `-` даёт ложные результаты. Заголовок — источник класса C: `source = 'window_title'`, доверие ниже.

### 7.4 Восстановление

```text
code <workspace_path>                      открыть папку
code -g <file_path>:<line>                 открыть файл на строке остановки
code -r <path>                             переиспользовать окно (reuse)
code --add <path>                          добавить папку в workspace
```

`code -g path:line` — лучшая демонстрационная фича проекта: **открыть файл на той самой строке, где работа была прервана**. Штатное восстановление окон так не умеет в принципе.

`code` должен быть в PATH. Если нет — resolver путей приложений (`App Paths` → ключи Uninstall → `%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe` → PATH). Не найден → шаг `failed` с причиной, остальные шаги выполняются.

`-r` (reuse) используется сознательно: без него каждое восстановление плодит новое окно VS Code.

---

## 8. Системные события

| | |
| --- | --- |
| **Класс** | A |
| **Запись** | `events` (kind = `session_start` / `session_end` / `system_sleep` / `system_wake`) |

```text
Microsoft.Win32.SystemEvents.PowerModeChanged    → Suspend / Resume
Microsoft.Win32.SystemEvents.SessionSwitch       → ConsoleConnect / ConsoleDisconnect /
                                                   SessionLock / SessionUnlock / RemoteConnect
Application.OnSessionEnding                      → выход из Windows / перезагрузка
WM_QUERYENDSESSION                               → ~5 секунд на синхронную запись снапшота
GetLastInputInfo                                 → idle-порог (границы сессии)
```

`SystemEvents` требует message loop (в WPF есть). `SessionSwitch` даёт блокировку экрана — это **не** конец сессии, если idle-порог ещё не превышен; решение принимает `SessionEngine`, а не коллектор.

### Критично: окно `WM_QUERYENDSESSION`

Windows даёт около 5 секунд до принудительного завершения. Снапшот перед перезагрузкой пишется **синхронно и быстро**:

```text
собрать состояние из кэша (не запускать git/UIA в этот момент)
одна транзакция, один батч
PRAGMA synchronous = NORMAL (не FULL — ради скорости в этом окне)
логирование — после записи, не до
```

Асинхронная очередь на запись в этот момент может не успеть: пользователь увидит пустой снапшот после перезагрузки.

---

## 9. Рабочие директории процессов

| | |
| --- | --- |
| **Класс** | C |
| **Способ** | PEB (раздел 6.2) или аргументы CommandLine |

Без админки CWD чужого процесса недоступен для elevated-процессов и AppContainer. Для процессов своего пользователя — доступен, но дорогой (чтение памяти). Поэтому CWD собирается **только для процессов-терминалов и для процессов, запущенных из известного workspace**, а не для всех подряд.

---

## 10. Сводная таблица

| Источник | Класс | Основной способ | Fallback | При недоступности |
| --- | --- | --- | --- | --- |
| Процессы | A | `CreateToolhelp32Snapshot` | `Process.GetProcesses()` (разово) | отказ = баг |
| Окна, заголовки | A | `EnumWindows` + `GetWindowText` | — | отказ = баг |
| Активное окно | A | `GetForegroundWindow` | — | отказ = баг |
| Файлы (git) | B | `git status --porcelain --no-optional-locks` | `FileSystemWatcher` | `available = 0` |
| Файлы (не git) | B | `FileSystemWatcher`, `Recent\*.lnk` | — | `file_activity` пуст |
| Git-состояние | B | `git.exe` (раздел 4) | — | «Git недоступен», статус «неясен» |
| Проект | A/B | git root → workspace → CWD → общий предок | — | проект не определён, события без `project_id` |
| Chrome title | A | заголовок окна | — | отказ = баг |
| Chrome URL | C | UIA → адресная строка | — | `url_available = 0`, UI: «URL недоступен» |
| Terminal CWD | B/C | CIM `CommandLine` | PEB | `source = 'unknown'` |
| VS Code файлы | B | `state.vscdb`, `User/History` | заголовок окна | `source = 'window_title'` |
| Системные события | A | `SystemEvents`, `OnSessionEnding` | — | отказ = баг |
| Пути приложений | B | `App Paths` → Uninstall → известные → PATH | — | шаг плана `failed` с причиной |

Правило для всех строк с fallback: **поле `source` в БД обязано отражать реальный способ получения.** Это позволяет и отлаживать, и не доверять слабым данным там, где важна точность.
