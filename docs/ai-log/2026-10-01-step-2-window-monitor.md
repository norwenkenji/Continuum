# 2026-10-01 - Шаг 2: WindowMonitor + дополнения spec (агент)

## Задача

Делегированная часть шага 2 (readme, «Порядок доведения до MVP»):

1. **WindowMonitor** - коллектор процессов и окон (WinAPI): фокусное окно каждые 2 с, старт/выход процессов диффом снапшотов, события ровно на изменение. Критерий шага: смена активного окна даёт ровно одно событие.
2. **Дополнения privacy по обновлённой спецификации** - §4.2 (`%LOCALAPPDATA%\Packages`, контейнеры UWP) и §5.1 (семейство GitHub-токенов `gh[opsur]_`, fine-grained `github_pat_`).

Шов (контракты `IObservationSource`/`Observation`, пайплайн, сессия, трей, DI) подготовлен вне делегации; подключение `WindowMonitor` в `CompositionRoot` - тоже вне делегации (интеграция).

## Агент

- **Модель:** Kimi-k3 (Moonshot AI)
- **Способ:** задача делегирована промптом ниже, отчёт получен и проверен независимым прогоном.

## Промпт (передан агенту)

```text
Ты - исполнитель части шага 2 проекта Continuum (WPF, .NET 10, локальная система
восстановления рабочего контекста). Рабочая директория: C:\need\projects\UTILS\Continuum -
git-репозиторий, ветка main. SDK: dotnet 10.0.401. Сборка сейчас: 0 ошибок,
100 тестов зелёные. Ошибок и предупреждений быть не должно и после тебя.

ОБЯЗАТЕЛЬНО ПРОЧИТАЙ ПЕРЕД РАБОТОЙ:
1. readme.md: «Бюджет ресурсов» (опрос окон 2–5 с; Process.GetProcesses() в цикле
   ЗАПРЕЩЁН - только CreateToolhelp32Snapshot или кэш), «Порядок доведения до MVP» шаг 2.
2. docs/specs/privacy-pipeline.md §4.2 и §5.1 - спецификация ОБНОВЛЕНА:
   §4.2 содержит «%APPDATA%\..\Local\Packages\*» (это то же, что
   %LOCALAPPDATA%\Packages), §5.1 - семейство GitHub-токенов gh[opsur]_ и github_pat_.
3. Контракты ядра (менять НЕЛЬЗЯ): src/Continuum.Core/Abstractions/ -
   Observable, ObservableKind, IObservationSource, Observation; EventKind в Core/Domain.
4. Существующие реализации: Infrastructure/Privacy/ExclusionSet.cs и
   Infrastructure/Privacy/SecretSanitizer.cs - их ты РАСШИРЯЕШЬ (это разрешено),
   не ломая существующие тесты.

КРИТЕРИЙ ГОТОВНОСТИ ШАГА: смена активного окна даёт РОВНО ОДНО событие.

ЗАДАЧА - ТРИ ЧАСТИ.

ЧАСТЬ 1. WindowMonitor - коллектор процессов и окон (WinAPI)
Каталог src/Continuum.App/Collectors/, namespace Continuum.Collectors
(НЕ Continuum.App.* - конфликт с классом Continuum.App, не компилируется).
P/Invoke - отдельно: Collectors/Native/NativeMethods.cs (internal static,
SetLastError, CharSet.Unicode, xml-doc по-русски).

Класс WindowMonitor : IObservationSource:
- Конструктор WindowMonitor(IClock clock, TimeSpan? pollInterval = null).
  Интервал по умолчанию - 2 секунды (бюджет readme: 2–5 с). Цикл - PeriodicTimer.
- Смена фокуса: GetForegroundWindow → GetWindowTextLengthW/GetWindowTextW (буфер
  512) → GetWindowThreadProcessId → имя процесса. Эмитит Observation(
  Observable(Kind=WindowTitle, Value=title, ProcessId, ApplicationName,
  ApplicationExePath, Timestamp=clock.UtcNow, ProjectRootPath=null),
  EventKind.AppFocused). hwnd=0 и пустые заголовки - пропускаем. Ровно одно
  событие на изменение пары (pid, title): повторный опрос без изменений - тишина.
- Старт/выход процессов: дифф снапшотов через CreateToolhelp32Snapshot +
  Process32FirstW/Process32NextW (НЕ Process.GetProcesses()). Эмитит Observation
  с EventKind.AppStarted/AppExited, Kind=WindowTitle, Value=null
  (командная строка произвольных процессов - не этот шаг).
  ApplicationExePath - best-effort: OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)
  + QueryFullProcessImageNameW; не получилось - null, без исключений.
- Exe-путь для фокусного окна - тем же best-effort способом.
- Надёжность: любые WinAPI-ошибки и гонки (процесс умер между вызовами)
  проглатываются с пропуском тика; событие Observed никогда не бросает
  в подписчиков; Start/Stop идемпотентны; Stop останавливает таймер.
- Собственный процесс НЕ отфильтровывай - это работа privacy-конвейера.

Чистую логику вынеси из WinAPI в тестируемые классы (тот же namespace):
- FocusTracker: состояние (предыдущие pid+title) → решение «эмитить/нет».
- ProcessListDiffer: два снапшота (pid→имя) → списки started/exited.

ЧАСТЬ 2. Дополнения privacy (spec уже обновлена - реализуй)
1) ExclusionSet: добавь неудаляемый дефолт §4.2 -
   Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages")
   (контейнеры UWP; префиксное сравнение покрывает «\*»). Существующие тесты
   не ломай.
2) SecretSanitizer: по обновлённой §5.1 замени паттерн ghp_[A-Za-z0-9]{36} на
   gh[opsur]_[A-Za-z0-9]{20,} и добавь github_pat_[A-Za-z0-9_]{20,}
   (маска по-прежнему ровно "***", таймаут regex 1 с).

ЧАСТЬ 3. Тесты (tests/Continuum.Tests/, namespace Continuum.Tests.Collectors /
Continuum.Tests.Privacy, xunit [Fact]/[Theory], using Xunit - глобального нет):
- FocusTrackerTests: смена (pid,title) → ровно одно событие; повтор того же
  состояния → нет события; пустой title → нет события; смена pid при том же
  title → событие; смена title при том же pid → событие.
- ProcessListDifferTests: появившиеся pid → started; исчезнувшие → exited;
  без изменений → пусто; первый снапшот после старта монитора не должен
  эмитить app_started для всех существующих процессов (базовый снапшот -
  точка отсчёта, не события).
- ExclusionSetTests (добавь кейсы в существующий файл): %LOCALAPPDATA%\Packages
  и любой его подкаталог исключены по умолчанию.
- SecretSanitizerTests (добавь кейсы в существующий файл): gho_, ghs_, ghr_,
  github_pat_ маскируются; ghp_ длиной 40 символов тоже (новый диапазон {20,}).

ЗАПРЕЩЕНО:
- git commit / push / reset / rebase / stash - дерево оставь незакоммиченным;
- регистрировать WindowMonitor в DI или трогать CompositionRoot.cs / App.xaml.cs -
  это интеграция вне твоей задачи;
- менять readme.md, docs/, .gitignore, csproj, контракты Core, чужие файлы шага 2
  (Runtime/, Pipeline/, Tray/, MainWindow*, чужие тесты). Разрешено править только
  Infrastructure/Privacy/ExclusionSet.cs, SecretSanitizer.cs и их тестовые файлы.
  Ошибка в чужом коде → раздел «Замечания к остальному коду» в отчёте;
- новые NuGet-пакеты; CJK-символы; P/Invoke вне NativeMethods.cs.
- Внимание: в App-проекте включён UseWindowsForms - осторожно с неоднозначными
  именами (Application, MessageBox, Timer); в новых файлах используй явные using и при
  необходимости квалификацию. Тестовый проход *_wpftmp.csproj игнорирует
  ImplicitUsings - пиши все using явно.

ПРОВЕРКА (выполнить и приложить вывод):
- dotnet build Continuum.sln → 0 ошибок, 0 предупреждений;
- dotnet test Continuum.sln → все зелёные (было 100 + твои новые);
- git status --short.

ОТЧЁТ В КОНЦЕ: созданные/изменённые файлы; итог build/test; отступления;
замечания к остальному коду, если есть.
```

## Что сделал агент

### Файлы созданы

| Файл | Содержимое |
| --- | --- |
| `src/Continuum.App/Collectors/Native/NativeMethods.cs` | Весь P/Invoke отдельно: `internal static`, `SetLastError`, `CharSet.Unicode`, xml-doc по-русски; user32 (фокус/заголовок/pid окна), kernel32 (Toolhelp32-снапшот, `OpenProcess`/`QueryFullProcessImageNameW`, `CloseHandle`) |
| `src/Continuum.App/Collectors/WindowMonitor.cs` | `IObservationSource`: `PeriodicTimer` 2 с; Start/Stop идемпотентны (Stop отменяет и дожидается цикл); ошибки тика проглатываются; `Observed` поднимается по invocation list с per-handler try/catch. Фокус: `GetForegroundWindow` → заголовок (буфер 512) → pid; ровно одно событие на смену пары (pid, title). Процессы: дифф Toolhelp32-снапшотов; `app_started`/`app_exited` с `Value=null`; exe-путь best-effort. Собственный процесс не фильтруется |
| `src/Continuum.App/Collectors/FocusTracker.cs` | Чистая логика «эмитить/нет» по паре (pid, title); мусорное чтение (пустой title, pid ≤ 0) не портит состояние; `Reset` |
| `src/Continuum.App/Collectors/ProcessListDiffer.cs` | Чистый дифф снапшотов (pid → имя); первый снапшот - база без событий; переиспользованный pid с другим именем → exit+start; выходные record'ы `ProcessChange`/`ProcessDiff` здесь же |
| `tests/Continuum.Tests/Collectors/FocusTrackerTests.cs` | 10 кейсов |
| `tests/Continuum.Tests/Collectors/ProcessListDifferTests.cs` | 6 кейсов |

### Файлы изменены (ровно четыре разрешённых)

| Файл | Изменение |
| --- | --- |
| `src/Continuum.App/Infrastructure/Privacy/ExclusionSet.cs` | Неудаляемый дефолт §4.2: `%LOCALAPPDATA%\Packages` (комментарием зафиксировано тождество с «`%APPDATA%\..\Local\Packages\*`») |
| `src/Continuum.App/Infrastructure/Privacy/SecretSanitizer.cs` | `ghp_[A-Za-z0-9]{36}` → `gh[opsur]_[A-Za-z0-9]{20,}` + добавлен `github_pat_[A-Za-z0-9_]{20,}`; маска ровно `***`, таймаут regex 1 с |
| `tests/Continuum.Tests/Privacy/ExclusionSetTests.cs` | +1 Fact: `Packages` и подкаталог исключены, граница каталога сохраняется |
| `tests/Continuum.Tests/Privacy/SecretSanitizerTests.cs` | +7 кейсов: семейство `gh[opsur]`, `ghp_` длиной 40, `github_pat_` |

### Проверка

- Агент: `dotnet build Continuum.sln` → 0 ошибок, 0 предупреждений; `dotnet test` → 124 пройдено, 0 не пройдено (было 100 + 24 новых); `git status --short` - только разрешённые файлы, чужое не тронуто, коммитов не было.
- Независимый повторный прогон после получения: build 0/0, тесты 124/124 - сходится.

### Живой прогон после интеграции

`WindowMonitor` зарегистрирован в `CompositionRoot`; приложение запущено, фокус переключался между окнами, процессы стартовали/умирали. Содержимое БД после прогона:

- `session_start` записан; висячая сессия после kill закрывается при следующем старте (crash-recovery, юнит-тесты).
- Смена фокуса → ровно одно событие `app_focused` с заголовком и приложением (критерий шага).
- `app_started`/`app_exited` от диффа снапшотов - в том числе видны стаб-переходы Store-программ (app execution alias: старт → handoff → выход стаба).
- `applications` наполняется upsert'ом с полным путём exe (напр., `C:\Program Files\Google\Chrome\Application\chrome.exe`).
- Собственный процесс Continuum в БД не попадает - отбрасывается privacy-конвейером, коллектор его не фильтрует (по заданию).

## Отступления агента

| Отступление | Оценка |
| --- | --- |
| `ApplicationExePath` эмитится только для `app_started` и фокусного окна; для `app_exited` всегда null (процесс уже завершился, `OpenProcess` закономерно вернёт ошибку) | Принято: физиология Windows, зафиксировано комментарием |
| `ProcessChange`/`ProcessDiff` объявлены в файле `ProcessListDiffer.cs`, а не отдельными файлами | Принято: выходные типы чистой логики, отдельные файлы не оправданы |
| Тесты сверх списка (пустой title не портит состояние, pid ≤ 0, `Reset`, переиспользование pid) | Принято: граничные случаи, иначе недоговорённые |

## Поправлено после агента

Ничего: сборка и тесты подтвердились независимым прогоном, правок не потребовалось.

## Замечания агента к остальному коду (ответы)

1. **Пайплайн маппит только Title/Path/Url/MetaJson; `app_started`/`app_exited` пишутся с пустыми полями** - корректно для шага 2: семантика на `application_id` + `kind`. Детали процессов (cwd, командная строка) придут с шагом 10 (терминалы); тогда и решим, что уйдёт в `meta_json`.
2. **`UpsertApplicationAsync` на каждое наблюдение** - да, UPSERT на каждую смену окна. Работоспособно (есть `ux_applications_name_exe`), но по бюджету «запись батчем» напрашивается кэш «name+exe → id» в памяти конвейера. Отложено: кэш появится при первом замере нагрузки или на шаге 4 (Session → Timeline), не раньше.

## Что отложено

- Коалесценция `applications`: одно и то же приложение с известным и с неизвестным exe_path сейчас даёт две строки (`('notepad', 'C:\…')` и `('notepad', '')` - разные ключи уникального индекса). На событиях не сказывается, но справочник «пыльный». Решение (матч по имени при неизвестном пути) - при первой чистке данных, не раньше шага 4.
- Правила §4.3 (BY_APP_NAME / BY_HOST / BY_PATH_SUBSTR) - шаг 9.
- CWD терминалов, командные строки - шаг 10.
