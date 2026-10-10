# 2026-10-05 - Шаг 3: Project + Git + file_activity (агент)

## Задача

Делегированная часть шага 3 (readme, «Порядок доведения до MVP»; критерий: «проект определяется по пути файла и по CWD; при отсутствии git - available = 0, без исключений»):

1. **ProcessRunner** - запуск внешних процессов с харденингом §4.3 (таймаут с убийством дерева, async-чтение потоков).
2. **GitClient** - единственный компонент, запускающий git.exe: поиск по §4.1, только читающие команды §4.2, окружение §4.3; нет git - `Unavailable`, без исключений.
3. **ProjectResolver** - определение проекта по пути: внешний git root, нормализация (§4.4).
4. **ProjectGitMonitor** - опрос зарегистрированных проектов, эмит file-наблюдений на изменение dirty-набора.

Швы (контракты `IProcessRunner`/`IGitClient`/`IProjectResolver`/`IProjectRegistry`, `Observation.FileChange/FileSource`, `FileChangeKind.Opened`, file-ветка пайплайна, `ProjectRegistry`) подготовлены вне делегации; подключение в `CompositionRoot` и живой прогон - тоже вне делегации (интеграция).

## Агент

- **Модель:** Kimi-k3 (Moonshot AI)
- **Способ:** задача делегирована промптом ниже, отчёт получен и проверен независимым прогоном.

## Промпт (передан агенту)

```text
Контекст: проект Continuum - локальный WPF-рекордер рабочего контекста (.NET 10, WPF + SQLite, local-first, русский UI). Репозиторий: git, ветка main. SDK: dotnet 10.0.401. Сборка сейчас: 0 ошибок, 0 предупреждений, 132 теста зелёные. Так должно остаться и после тебя.

ОБЯЗАТЕЛЬНО ПРОЧИТАЙ ПЕРЕД РАБОТОЙ:
1. readme.md - «Бюджет ресурсов», «Порядок доведения до MVP» шаг 3.
2. docs/specs/data-sources.md §3 и §4 целиком - это нормативная спецификация для твоей задачи.
3. Контракты ядра (уже в дереве, реализация - твоя):
   - src/Continuum.Core/Abstractions/IProcessRunner.cs - ProcessResult + RunAsync
   - src/Continuum.Core/Abstractions/IGitClient.cs - DirtyFile, GitStateInfo, три метода
   - src/Continuum.Core/Abstractions/IProjectResolver.cs - ResolveRoot
   - src/Continuum.Core/Abstractions/Observation.cs - FileChange/FileSource
   - src/Continuum.Core/Domain/Enums.cs - FileChangeKind (есть Opened), FileActivitySource

## Что сделать

### 1. ProcessRunner (src/Continuum.App/Infrastructure/Processes/ProcessRunner.cs)

Реализация IProcessRunner: UseShellExecute=false, CreateNoWindow=true, перенаправление stdout/stderr с АСИНХРОННЫМ чтением обоих потоков (иначе взаимоблокировка на полном буфере), окружение через ProcessStartInfo.Environment, таймаут (параметр) и внешняя отмена - Kill(entireProcessTree: true). Ненулевой код выхода - это данные (ProcessResult.ExitCode), не исключение. Таймаут - ProcessResult.TimedOut=true.

### 2. GitClient (src/Continuum.App/Infrastructure/Git/GitClient.cs)

Реализация IGitClient - единственный компонент, запускающий git.exe:

- FindGitExe(): поиск по §4.1 - PATH (where git), HKLM\SOFTWARE\GitForWindows\InstallPath, HKCU то же, %LOCALAPPDATA%\Programs\Git\cmd\git.exe, %LOCALAPPDATA%\GitHubDesktop\app-*\resources\app\git\cmd\git.exe (свежайший app-*), %ProgramFiles%\Git\cmd\git.exe. Результат кэшируется на время жизни процесса. Конструкторный параметр gitExePath - шов для тестов (null - обычный поиск).
- Только читающие команды §4.2: rev-parse --show-toplevel, rev-parse --abbrev-ref HEAD, log -1 --format=%H%x09%ct%x09%s, status --porcelain. Каждый вызов - с префиксом «-c credential.helper=», окружением §4.3 (GIT_TERMINAL_PROMPT=0, GIT_OPTIONAL_LOCKS=0, GCM_INTERACTIVE=never, LC_ALL=C) и таймаутом 5 с.
- Porcelain-разбор - отдельный чистый класс PorcelainParser: ??->Untracked, A->Added, D->Deleted, R/C->Deleted(старое)+Added(новое), M/T/U->Modified; кавычки вокруг путей снимаются; битые строки молча пропускаются.
- НИКОГДА не бросает: git не найден, путь не репозиторий, таймаут, любой сбой - GitStateInfo.Unavailable / null. Это критерий шага: «при отсутствии git - available = 0, без исключений».

### 3. ProjectResolver (src/Continuum.App/Infrastructure/Git/ProjectResolver.cs)

Реализация IProjectResolver по §4.4: подъём от файла/каталога вверх, маркер - .git (каталог ИЛИ файл-worktree), корень - САМЫЙ ВЕРХНИЙ (вложенные репозитории - один проект); bare-репозитории не проекты. Нормализация: GetFullPath, обрезка завершающего разделителя, ToLowerInvariant, subst-диски раскрываются через QueryDosDeviceW. P/Invoke - только в src/Continuum.App/Infrastructure/Git/Native/GitNativeMethods.cs. Никогда не бросает.

### 4. ProjectGitMonitor (src/Continuum.App/Collectors/ProjectGitMonitor.cs)

IObservationSource: опрос корней из IProjectRegistry.RegisteredRoots раз в 10 с (конструкторный pollInterval для тестов). Дифф dirty-набора между тиками - отдельный чистый класс GitStateDiffer: первый снапшот - базовый (без эмиссий), появившиеся и сменившие kind пути - события, исчезнувшие молчат. Недоступный корень пропускается с сохранением прежнего состояния, забытые корни вычищаются. Эмит: Observation(Observable(FilePath, АБСОЛЮТНЫЙ путь (root + относительный), null, null, null, clock.UtcNow, ProjectRootPath=root), FileChanged, kind, FileActivitySource.Git). Start/Stop идемпотентны, никогда не бросает.

### 5. Тесты (tests/Continuum.Tests/)

- Git/PorcelainParserTests.cs - все виды XY-статусов, кавычки, переименования, битые строки.
- Collectors/GitStateDifferTests.cs - базовый снимок молчит, появление/смена kind/исчезновение.
- Git/GitClientTests.cs - фейковый IProcessRunner: точные аргументы команд, окружение §4.3, таймауты, detached HEAD, репозиторий без коммитов, git не найден.
- Git/ProjectResolverTests.cs - реальные temp-каталоги: вложенные репозитории, файл-.git, bare, вне репозитория.
- Git/GitClientIntegrationTests.cs - реальный git во временном репозитории %TEMP%; git не установлен - тест молча проходит.

## Жёсткие ограничения

- НЕ трогай: Continuum.Core/**, readme.md, docs/**, *.csproj, *.sln, CompositionRoot.cs, App.xaml.cs, ObservationPipeline.cs, SessionRuntime.cs, чужие тесты, файлы вне твоей задачи. plan.md не существует в репо - не ищи.
- НЕ делай git-коммитов и git-команд, меняющих состояние репозитория (add/commit/push/reset/checkout). git status/diff - можно. Временные репозитории для тестов - в %TEMP%, удалять за собой.
- Новые NuGet-пакеты запрещены.
- P/Invoke - только в файлах *NativeMethods.cs.
- Комментарии и строки - на русском. НИГДЕ не используй длинное тире «-» (U+2014), только дефис «-». Это критично.
- Файлы - UTF-8 без BOM.
- Пространства имён: Continuum.Infrastructure.*, Continuum.Collectors - НЕ Continuum.App.*.
- После работы: dotnet build Continuum.sln (0 ошибок, 0 предупреждений) и dotnet test Continuum.sln (все зелёные) - приложи хвост вывода.

## Отчёт

Перечисли созданные файлы с кратким описанием, итог build/test, отступления от задания (если были) и «Замечания к остальному коду» - заметил чужое, что стоит поправить (не исправляй сам).
```


## Что сделал агент

### Файлы созданы

| Файл | Содержимое |
| --- | --- |
| `src/Continuum.App/Infrastructure/Processes/ProcessRunner.cs` | `IProcessRunner`: `UseShellExecute=false`, `CreateNoWindow=true`, async-чтение обоих потоков (без взаимоблокировки), окружение через `psi.Environment`, таймаут и внешняя отмена - `Kill(entireProcessTree: true)`; ненулевой код выхода - данные, не исключение |
| `src/Continuum.App/Infrastructure/Git/GitClient.cs` | Кэшированный поиск git.exe по §4.1 (PATH → HKLM → HKCU → `%LOCALAPPDATA%\Programs\Git` → GitHubDesktop свежайший `app-*` → ProgramFiles); все вызовы с `-c credential.helper=`, окружением §4.3 и таймаутом 5 с; любая проблема - `Unavailable`/`null`, никогда не бросает |
| `src/Continuum.App/Infrastructure/Git/PorcelainParser.cs` | Чистый разбор porcelain v1: `??`→Untracked, `A`→Added, `D`→Deleted, `R`/`C`→Deleted(old)+Added(new), `M`/`T`/`U`/прочее→Modified; снятие кавычек; битые строки молча пропускаются |
| `src/Continuum.App/Infrastructure/Git/ProjectResolver.cs` | Подъём по маркерам `.git` (каталог И файл-worktree), корень - самый верхний (вложенные - один проект), bare не матчатся (комментарий), канонизация + subst через `QueryDosDeviceW`, не бросает |
| `src/Continuum.App/Infrastructure/Git/Native/GitNativeMethods.cs` | `QueryDosDeviceW` (`SetLastError`, Unicode) |
| `src/Continuum.App/Collectors/GitStateDiffer.cs` | Чистый дифф dirty-набора (path→kind): появившиеся/сменившие kind, исчезнувшие молчат, первый снапшот - база |
| `src/Continuum.App/Collectors/ProjectGitMonitor.cs` | Опрос корней реестра раз в 10 с; события `FileChanged`/`FileActivitySource.Git` с абсолютным путём и `ProjectRootPath`; недоступный корень пропускается с сохранением состояния; забытые корни вычищаются |
| `tests/Continuum.Tests/Git/PorcelainParserTests.cs` | 16 тестов |
| `tests/Continuum.Tests/Collectors/GitStateDifferTests.cs` | 6 тестов |
| `tests/Continuum.Tests/Git/GitClientTests.cs` | 15 тестов на фейковом `IProcessRunner` (точные аргументы, окружение, таймауты, detached HEAD, репозиторий без коммитов) |
| `tests/Continuum.Tests/Git/ProjectResolverTests.cs` | 7 тестов на реальных temp-каталогах |
| `tests/Continuum.Tests/Git/GitClientIntegrationTests.cs` | Полный жизненный цикл на реальном git в %TEMP% (skip при отсутствии git) |

### Проверка

- Агент: `dotnet build` → 0 ошибок, 0 предупреждений; `dotnet test` → 183 пройдено (было 132 + 51 новый); `git status` - чужие файлы не тронуты, коммитов не было.
- Независимый повторный прогон: build 0/0, тесты 183/183 - сходится.

## Отступления агента (оба приняты, спека обновлена)

| Отступление | Проверка | Решение |
| --- | --- | --- |
| `symbolic-ref --short HEAD` как fallback ветки: на репозитории без коммитов `rev-parse --abbrev-ref HEAD` падает с кодом 128 | Воспроизведено | Принято: строго читающая plumbing-команда; §4.2 дополнена |
| `--no-optional-locks` перенесён в глобальную позицию (`git -c … --no-optional-locks status --porcelain`): как флаг `status` не существует | Воспроизведено на git 2.54: флаговая позиция даёт exit 129, глобальная - 0 | Принято; §4.2, таблица §3 и алгоритм «Где я остановился?» в readme исправлены |

## Поправлено при интеграции

1. **Кириллица в именах dirty-файлов** (замечание агента №3 подтверждено опытом): по умолчанию git отдаёт не-ASCII пути восьмеричными эскейпами (`"\320\275…"`), которые парсер не разбирает. Решение: вызов `status` теперь с `-c core.quotePath=false` - пути приходят сырыми UTF-8, кавычки вокруг имён с пробелами парсер снимает штатно. Покрыто кейсом «новый файл.txt» в интеграционном тесте.
2. **Кодировка stdout/stderr в `ProcessRunner`**: `Process` по умолчанию декодирует потоки в OEM-кодировке консоли - кириллица из git превращалась в мойбак. Теперь `StandardOutputEncoding/StandardErrorEncoding = UTF-8` (контракт runner'а; единственный потребитель - git, который говорит UTF-8). Без этого интеграционный тест с кириллицей красный.
3. Мелочь: опечатка в ссылке на раздел спеки в комментарии (`§0.3` → `§4.3`).

## Живой прогон после интеграции

`GitClient`/`ProjectResolver`/`ProjectRegistry`/`ProjectGitMonitor` зарегистрированы в `CompositionRoot`; для регистрации проекта добавлен dev-шов `CONTINUUM_WATCH_PATH` (пользовательского UI регистрации в MVP нет - пути придут из шагов 7/10).

Сценарий: временный репозиторий с коммитом → запуск приложения с `CONTINUUM_WATCH_PATH` → базовый тик (10 с) → правка `a.txt` + новый `отчёт.txt` → второй тик → чтение БД:

- `projects`: проект создан с нормализованным корнем (нижний регистр, без завершающего разделителя).
- `file_activity`: обе строки с `session_id`, `project_id`, абсолютным путём (кириллица читается), `change_kind` = `modified`/`untracked`, `source` = `git`.
- `events`: `file_changed` с `project_id`.

Первый прогон событий не показал - не дефект: изменения попали ДО базового тика и вошли в базу (механика подтверждена отдельным прогоном монитора: эмитятся только изменения против базы).

## Замечания агента к остальному коду (ответы)

1. **Спека §4.2 устарела** (позиция `--no-optional-locks`, fallback ветки) - принято, спека обновлена (см. «Отступления»).
2. **Расхождение канонических форм**: резолвер раскрывает subst-диски, `GetRepositoryRootAsync` - нет (git возвращает путь как есть). Потребителей `GetRepositoryRootAsync` сейчас нет (реестр работает через резолвер), поэтому расхождение латентное. Отложено: если появится потребитель, сравнивающий ключи с реестром - вынести общий канонизатор.
3. **Парсер разэкранирует только `\"` и `\\`** - закрыто поправкой интеграции №1 (`core.quotePath=false`).

## Ограничения (принятые)

- subst-разрешение покрыто только косвенно (обычные диски проходят без изменений): subst нельзя создать внутри тестового процесса без внешней команды.

## Что отложено

- `FileSystemWatcher` на корнях проектов (§3.1) и `Recent\*.lnk` (§3.2) - шаг 4 (вместе со снапшотами; критерий шага 3 покрыт git-источником).
- События `git_commit`/`git_branch_switch` в хронологию - шаг 4/5 (нужны Stopped Engine; данные для них `GitClient` уже отдаёт).
- `git_remote` в `projects` - читается со снапшотами (шаг 4).
