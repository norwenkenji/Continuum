# 2026-11-06 - Шаг 7: адаптер VS Code (агент)

## Задача

Шаг 7 дорожной карты (readme): адаптер VS Code. Критерий: «Открытые файлы и последняя строка читаются из фактов, а не угадываются по заголовкам». Спецификация - docs/specs/data-sources.md §7 (источники, правила доступа, восстановление `code -g path:line`).

Разделение: швы (контракт `IOpenFilesProvider`, снапшот с `open_files`, wiring, живой прогон, readme) - вне делегации; агенту - сам адаптер (чтение History/workspaceStorage/globalStorage, события сохранений, открытые файлы с позициями курсора) + тесты.

## Вне делегации (швы)

- `IOpenFilesProvider` (Core/Abstractions): `GetOpenFilesAsync()` - Snapshotter спрашивает открытые файлы на момент снапшота, а не получает их потоком событий.
- `Snapshotter`: опциональные параметры `IOpenFilesProvider` + `IProjectResolver`; снапшот теперь включает `open_files` - пути через privacy-фильтр (как dirty-файлы git), проект через резолвер git root + реестр. Провайдера нет / недоступен - снапшот без open_files (честная деградация).
- Регистрация в CompositionRoot: адаптер как IObservationSource и IOpenFilesProvider (один синглтон на обе роли); dev-шов `CONTINUUM_VSCODE_ROOTS=<путь>;<путь>` - корни для живой проверки вместо дефолтных.
- 2 своих теста Snapshotter: open_files с проектом и privacy-фильтрацией; без провайдера - без open_files.

## Агент

- **Модель:** GLM-5.3-Prime (Z.ai)
- **Способ:** задача делегирована промптом ниже, отчёт получен и проверен независимым прогоном.

## Промпт (передан агенту)

```text
Контекст: проект Continuum - локальный WPF-рекордер рабочего контекста (.NET 10, WPF + SQLite, local-first, русский UI). Репозиторий: git, ветка main. SDK: dotnet 10.0.401. Сборка сейчас: 0 ошибок, 0 предупреждений, 260 тестов зелёные. Так должно остаться и после тебя. VS Code на машине НЕ установлен - все тесты только на фикстурах в %TEMP%.

ОБЯЗАТЕЛЬНО ПРОЧИТАЙ ПЕРЕД РАБОТОЙ:
1. docs/specs/data-sources.md §7 целиком - нормативка для твоей задачи (источники, правила доступа, форматы).
2. Контракты ядра (уже в дереве, НЕ редактируй):
   - src/Continuum.Core/Abstractions/IObservationSource.cs, Observation.cs, Observable.cs
   - src/Continuum.Core/Abstractions/IOpenFilesProvider.cs - НОВЫЙ шов: GetOpenFilesAsync() -> IReadOnlyList<OpenFile>
   - src/Continuum.Core/Abstractions/IProjectResolver.cs - ResolveRoot(path) -> нормализованный git root | null
   - src/Continuum.Core/Domain/Snapshot.cs - OpenFile(Id, SnapshotId, Path, ProjectId, ApplicationId, Line, LastModified, Source)
   - src/Continuum.Core/Domain/Enums.cs - FileActivitySource.VsCodeHistory уже есть; FileChangeKind
   - src/Continuum.Core/Abstractions/IClock.cs
3. src/Continuum.App/Collectors/ProjectGitMonitor.cs - образец коллектора (Start/Stop идемпотентны, первый тик базовый, Emit с защитой подписчиков).
4. src/Continuum.App/Snapshots/Snapshotter.cs - как используется IOpenFilesProvider (вызывается на момент снапшота).

## Что сделать

### 1. VSCodeAdapter (src/Continuum.App/Adapters/VSCode/, namespace Continuum.Adapters.VSCode)

Один класс VSCodeAdapter - сразу два интерфейса: IObservationSource (события сохранений) и IOpenFilesProvider (открытые файлы для снапшота). Разнеси на файлы по смыслу: VSCodeAdapter.cs (цикл, эмит), VSCodeStateReader.cs (чтение диска/SQLite), VSCodePathUtil.cs (URI -> путь) - имена можешь менять, пространство Continuum.Adapters.VSCode.

Конструктор: (IClock clock, ILogger<VSCodeAdapter> logger, IReadOnlyList<string>? roots = null, IProjectResolver? resolver = null, TimeSpan? pollInterval = null). roots null - дефолт: %APPDATA%\Code, %APPDATA%\Code - Insiders, %APPDATA%\VSCodium, %APPDATA%\Cursor (спека §7.2: адаптер принимает список корней, не один хардкод). Внутри корня данные лежат в <root>\User\History и <root>\User\workspaceStorage, <root>\User\globalStorage.

Формат данных (реальные форматы VS Code):

a) <root>\User\History\<hash>\entries.json:
   {
     "version": 1,
     "resource": "file:///d%3A/need/projects/x/readme.md",
     "entries": [ { "id": "ABC123.md", "source": "..." } ]
   }
   resource - URI отслеживаемого файла (percent-encoded: %3A = ':', кириллица тоже экранирована). entries[].id - имена файлов-снапшотов, лежащих В ТОЙ ЖЕ папке; их mtime = момент сохранения. Последнее сохранение файла = max mtime среди файлов из entries. entries может быть пуст - тогда mtime не определено (skip такой файл в событиях, но path валиден для open_files).

b) <root>\User\workspaceStorage\<hash>\workspace.json:
   { "folder": "file:///d%3A/need/projects/x" }
   (может быть "workspace" вместо "folder" для multi-root, может отсутствовать)

c) state.vscdb - SQLite, одна таблица ItemTable(key TEXT PRIMARY KEY, value BLOB).
   - workspaceStorage\<hash>\state.vscdb: key "workbench.state" - сериализованный блоб; внутри встречаются JSON-островки вида "resource":"file:///..." и "selectionStartLineNumber":42 (позиция курсора). Чтение блоба как UTF-8 текст + регексы - best-effort: пары ресурс -> последняя selectionStartLineNumber перед следующим ресурсом. Не распарсилось - строк нет, это не ошибка.
   - globalStorage\state.vscdb: key "history.recentlyOpenedPathsList" - JSON {"entries":[{"folderUri":"file:///..."},{"fileUri":"file:///..."}]} - недавние папки и файлы.
   Доступ к state.vscdb: Microsoft.Data.Sqlite, строка подключения "Data Source=<путь>;Mode=ReadOnly" (VS Code держит файл открытым). Если открыть не удалось - скопировать файл в %TEMP% и читать копию; не удалось и это - тихо пропустить. КАТЕГОРИЧЕСКИ не писать в state.vscdb и в User\History (спека §7.2).

d) URI -> локальный путь: file:///d%3A/path/%D0%BE%D1%82%D1%87%D0%B5%D1%82.md -> d:\path\отчёт.md. Используй System.Uri (new Uri(u).LocalPath) с fallback на ручную percent-декодировку (Uri.UnescapeDataString); файл:///c%3A/... -> c:\....

### 2. IObservationSource: события сохранений

Опрос раз в 45 с по умолчанию (спека §0.3: 30-60 с; pollInterval - параметр для тестов). За тик по каждому корню:
- перечислить <root>\User\History\*\entries.json; для каждого: путь файла (resource -> локальный путь) и время последнего сохранения (max mtime файлов из entries; нет entries - время неизвестно);
- дифф с прошлым тиком: файл появился ИЛИ время сохранения стало позже -> эмит;
- первый тик - базовый, молча;
- Observation: Observable(Kind: FilePath, Value: АБСОЛЮТНЫЙ путь, ProcessId/ApplicationName/ApplicationExePath: null, Timestamp: clock.UtcNow, ProjectRootPath: resolver.ResolveRoot(путь) если resolver задан, иначе null), EventKind.FileChanged, FileChange: FileChangeKind.Modified, FileSource: FileActivitySource.VsCodeHistory.
- корня нет / битый entries.json (не JSON, нет resource) - молча пропустить, НИКОГДА не бросать.
- Start/Stop идемпотентны, Emit с защитой подписчиков (как ProjectGitMonitor).

### 3. IOpenFilesProvider: открытые файлы для снапшота

GetOpenFilesAsync - чтение по требованию (вызывается Snapshotter'ом раз в 5 минут, НЕ в цикле опроса):
- по каждому корню: перечислить <root>\User\workspaceStorage\<hash>\:
  - state.vscdb (через reader) -> из workbench.state: пары (ресурс, line) - эти файлы открыты, line - позиция курсора;
  - entries.json из History для lastModified (max mtime) - если файла там нет, LastModified = null;
  - результат: OpenFile(0, 0, path, ProjectId: null, ApplicationId: null, Line, LastModified, Source: "vscode_state");
- если workspaceStorage ничего не дал: fallback globalStorage\state.vscdb -> history.recentlyOpenedPathsList -> все fileUri -> OpenFile(..., Line: null, Source: "vscode_state");
- дедуп по пути (OrdinalIgnoreCase), сортировка не важна;
- ничего не найдено / нет корней -> пустой список, без исключений;
- ProjectId заполнять НЕ надо (это делает Snapshotter через резолвер).

### 4. Тесты (tests/Continuum.Tests/Adapters/VSCodeAdapterTests.cs)

Фикстуры в %TEMP% (builder-класс в тестах): фейковый корень с User\History\<hash>\entries.json + файлы-снапшоты (mtime через File.SetLastWriteTime), User\workspaceStorage\<hash>\workspace.json + state.vscdb (СОЗДАВАТЬ через Microsoft.Data.Sqlite: CREATE TABLE ItemTable, INSERT workbench.state блобом с JSON-островками), User\globalStorage\state.vscdb аналогично. pollInterval 50 мс, ожидания WaitFor-циклом с таймаутом 15 с (образец - tests/Continuum.Tests/Collectors/RecentFilesMonitorTests.cs).

Кейсы:
- первый тик молчит; новое сохранение после базы (File.SetLastWriteTime позже) -> ровно одно событие Modified/VsCodeHistory с расшифрованным путём;
- кириллица в resource URI (percent-encoded) -> путь в событии читается по-русски;
- без изменений - тишина;
- GetOpenFiles: line из фикстуры workbench.state (ресурс + selectionStartLineNumber), путь декодирован, Source "vscode_state", LastModified из History;
- fallback: нет workspaceStorage-данных -> fileUri из recentlyOpenedPathsList, line null;
- корень не существует -> пусто, без исключений; битый entries.json (не JSON) -> пропуск;
- state.vscdb занят (открой файл с FileShare.None в тесте) -> копия в %TEMP% читается, данные получены; полностью недоступен -> пусто, без исключений;
- два корня - читаются оба.

## Жёсткие ограничения

- НЕ трогай: Continuum.Core/** (все швы есть), readme.md, docs/**, *.csproj, *.sln, CompositionRoot.cs (регистрацию сделает интегратор), App.xaml.cs, Snapshotter.cs, RestoreService.cs, чужие тесты.
- НЕ делай git-коммитов и git-команд, меняющих состояние репозитория. git status/diff - можно. Фикстуры - в %TEMP%, удалять за собой.
- Новые NuGet-пакеты запрещены (Microsoft.Data.Sqlite уже в проекте).
- P/Invoke и COM - только в *NativeMethods.cs (здесь не нужны).
- Комментарии и строки - на русском. НИГДЕ не используй длинное тире «-» (U+2014), только дефис «-». Это критично. Без CJK-символов.
- Файлы - UTF-8 без BOM.
- Пространства имён: Continuum.Adapters.VSCode - НЕ Continuum.App.*.
- После работы: dotnet build Continuum.sln (0 ошибок, 0 предупреждений) и dotnet test Continuum.sln (все зелёные) - приложи хвост вывода.

## Отчёт

Перечисли созданные/изменённые файлы с кратким описанием, итог build/test, отступления от задания (если были) и «Замечания к остальному коду» (что заметил чужого - не исправляй, только перечисли).
```

## Что сделал агент

### Файлы созданы

| Файл | Содержимое |
| --- | --- |
| `src/Continuum.App/Adapters/VSCode/VSCodeAdapter.cs` | Обе роли: опрос History (45 с, базовый тик молча, дифф «появился / mtime позже»), эмит с ProjectRootPath через резолвер; GetOpenFilesAsync (workspaceStorage -> fallback globalStorage), дедуп, никогда не бросает |
| `src/Continuum.App/Adapters/VSCode/VSCodeStateReader.cs` | Чтение entries.json / workbench.state (регексы JSON-островков) / recentlyOpenedPathsList; state.vscdb в ReadOnly, при отказе копия в %TEMP%\continuum-vscode |
| `src/Continuum.App/Adapters/VSCode/VSCodePathUtil.cs` | URI -> путь: Uri.LocalPath + нормализация + ручная percent-декодировка |
| `tests/.../VSCodeAdapterTests.cs` | 11 тестов на фикстурах в %TEMP% (state.vscdb создаются через Microsoft.Data.Sqlite) |

### Проверка

- Агент: build 0/0, тесты 271/271 (260 + 11), три прогона стабильно зелёные.
- Независимо: build 0/0, 271/271 два прогона - сходится.

## Отступления агента (приняты)

1. Тест «state.vscdb занят -> копия в %TEMP% читается» невоспроизводим на Windows: при любом локе, валящем ReadOnly-открытие, File.Copy тоже падает (GENERIC_READ у e_sqlite3 и CopyFile совпадает). Вместо него два теста: реальный паттерн «VS Code держит открытым» (ReadWrite + FileShare.Read - читается напрямую) и полный эксклюзивный лок (FileShare.None - пусто без исключений). Цепочка с копией в коде есть, интеграционно непокрыта - некому её вызвать физически.
2. `Uri.LocalPath` для percent-encoded буквы диска выдаёт Unix-подобный путь (`/d:/path`); закрыто нормализацией в VSCodePathUtil. Без неё расшифрованный путь был бы битым.
3. Базовый тик в тестах ждётся сном 700 мс при интервале 50 мс (запас ~14 тиков) - сентинел-механика RecentFilesMonitorTests тут неприменима, нет наблюдаемого маркера тика.

## Поправлено при интеграции

1. CompositionRoot: адаптер зарегистрирован как IObservationSource + IOpenFilesProvider (один синглтон), Snapshotter получил openFiles/projectResolver. Dev-шов `CONTINUUM_VSCODE_ROOTS` - корни для живой проверки.
2. `using System` в CompositionRoot (Environment для dev-шва).

## Замечания агента (ответы)

1. Snapshotter.Stop ждёт цикл 2 с против 5 с у мониторов - принято: тик снапшота лёгкий (SQLite локальная), при тяжёлом тике подниму.
2. Пустые папки continuum-*-tests в %TEMP% от тестов - почищено агентом руками в этом прогоне; добавил себе в список ревизии.
3. Отдельная строка подключения вместо SqliteConnectionFactory (та всегда WAL + ReadWriteCreate) - так и задумано: спека §7.2 требует ReadOnly для чужих state.vscdb.

## Живой прогон после интеграции

VS Code на машине нет - прогон на фейковом корне (builder-обвязка в %TEMP%): entries.json с кириллическим resource URI, снапшот-файл с mtime, workspaceStorage\state.vscdb с workbench.state «resource + selectionStartLineNumber:42». Запуск приложения с dev-швами `CONTINUUM_WATCH_PATH` + `CONTINUUM_VSCODE_ROOTS` + `CONTINUUM_EXIT_AFTER_S`:

- `file_activity`: событие сохранения - `отчёт.md | modified | vs_code_history`, кириллица читается, project_id привязан;
- `open_files` в снапшоте: `отчёт.md | line=42 | vscode_state` - строка курсора из факта;
- адаптер в изоляции: ровно одно событие на новое сохранение, тишина без изменений.

Оговорки прогона (честно): первый заход казался «без событий» - причина в моём SQL-запросе: EnumCodec пишет источник как `vs_code_history` (с подчёркиванием), а искал я `vscode_history`; строки были с первого рабочего прогона. Имитация сохранения из PowerShell требует `[IO.File]::SetLastWriteTime` (C#-синтаксис `File.SetLastWriteTime` в PS не работает) - плюс запас от базового тика: касание на +65 с при тике базы ~47 с.

Восстановление `code -g path:line` собирается из готовых механизмов (хост `code` через ApplicationPathResolver + аргумент из open_files); на этой машине code.exe нет - шаг плана честно падает с «Приложение не найдено», остальные шаги живы (покрыто тестами шага 6).

## Что отложено

- Связка «open_files -> хост code -g» в UI/RestorePlan-обвязке - шаг 8 (демо-режим соберёт hosts из фактов сессии).
- Глубокий разбор workbench.state (точные позиции всех редакторов) - best-effort регексы достаточно для MVP; MS-блоб нестабилен между версиями VS Code.
