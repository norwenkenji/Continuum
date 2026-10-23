# 2026-10-06 - Шаг 5: Stopped Engine (агент)

## Задача

Шаг 5 дорожной карты (readme): Stopped Engine - реализация таблицы истинности «Где я остановился?», критерий: юнит-тесты на всех ветках (dirty / clean+commit / нет git). Плюс перенесённый долг шага 4: события `git_commit`/`git_branch_switch` в хронологию - без них ветка «последнее событие = commit» таблицы была недостижима.

Разделение: швы (enum `StoppedStatus`, запись `StoppedVerdict`, `ObservableKind.GitRef`, read-методы репозитория, маппинг GitRef в пайплайне, спека privacy) - вне делегации; агенту - `GitHeadDiffer` + расширение `ProjectGitMonitor` и сам `StoppedEngine` с тестами.

## Вне делегации (швы)

- `StoppedStatus` (Unclear/MaybeUnfinished/Completed) и `StoppedVerdict` в Core/Domain - вердикт с фактами, из которых выведен.
- `ObservableKind.GitRef` - короткая строка-факт из git (ветка, «hash subject»); пайплайн пишет её в `events.title`; спека privacy-pipeline §2/§3 дополнена (по чувствительности равен заголовку окна).
- `IRepository.GetSessionFileActivityAsync` и `GetProjectAsync` (+Sqlite +Fake) - движку нужны «файлы за сессию» и проект по id.
- `FakeRepository.GetSessionEventsAsync` теперь сортирует по времени - как Sqlite (замечание агента №3 принято сразу).

## Агент

- **Модель:** Kimi-k3 (Moonshot AI)
- **Способ:** задача делегирована промптом ниже, отчёт получен и проверен независимым прогоном.

## Промпт (передан агенту)

```text
Контекст: проект Continuum - локальный WPF-рекордер рабочего контекста (.NET 10, WPF + SQLite, local-first, русский UI). Репозиторий: git, ветка main. SDK: dotnet 10.0.401. Сборка сейчас: 0 ошибок, 0 предупреждений, 204 теста зелёные. Так должно остаться и после тебя.

ОБЯЗАТЕЛЬНО ПРОЧИТАЙ ПЕРЕД РАБОТОЙ:
1. readme.md - раздел «Детерминированный алгоритм "Где я остановился?"» (таблица истинности) и «Бюджет ресурсов».
2. Контракты ядра (уже в дереве, НЕ редактируй их):
   - src/Continuum.Core/Domain/StoppedVerdict.cs - StoppedVerdict + StoppedStatus (Unclear/MaybeUnfinished/Completed)
   - src/Continuum.Core/Domain/Enums.cs - EventKind (есть GitCommit, GitBranchSwitch), ObservableKind лежит в src/Continuum.Core/Abstractions/ObservableKind.cs (есть GitRef - пишется в events.title)
   - src/Continuum.Core/Interfaces/IRepository.cs - есть GetLastSessionAsync, GetSessionEventsAsync, GetSessionFileActivityAsync, GetProjectAsync
   - src/Continuum.Core/Abstractions/IGitClient.cs - GetStateAsync(root) отдаёт Branch, HeadCommit, HeadSubject, HeadTimestamp, DirtyFiles
3. src/Continuum.App/Collectors/ProjectGitMonitor.cs и GitStateDiffer.cs - твой код с шага 3, его и расширяем.

## Что сделать

### 1. События git_commit / git_branch_switch в ProjectGitMonitor

Сейчас монитор диффит только dirty-набор. Добавь отслеживание HEAD и ветки:

- На каждый корень храни предыдущие (Branch, HeadCommit) рядом с GitStateDiffer (отдельная маленькая запись/класс, например GitHeadDiffer - чистая логика без git, как GitStateDiffer).
- Первый тик по корню - базовый: запоминаем, НЕ эмитим (как у dirty-диффа).
- Ветка изменилась (была A, стала B, обе не null) - событие GitBranchSwitch.
- HeadCommit изменился при той же ветке (ветка не менялась, хэш сменился, оба не null) - событие GitCommit.
- Если в одном тике сменились и ветка, и хэш - только GitBranchSwitch (переключение ветки уже объясняет смену HEAD).
- Переход в detached HEAD (Branch стал null) - не событие, но состояние запоминаем; выход из detached (ветка появилась) - GitBranchSwitch от null не делаем: если прежняя ветка null - не эмитим (нечего назвать «с чего»), просто запоминаем. Задокументируй в комментарии.
- Недоступный корень (state.Available == false) - состояние не трогаем, как сейчас.
- Эмит Observation: Observable(Kind: ObservableKind.GitRef, Value: для коммита "<short7> <subject>" (hash 7 символов), для ветки "<from> -> <to>", ProcessId/ApplicationName/ApplicationExePath: null, Timestamp: clock.UtcNow, ProjectRootPath: root), EventKind.GitCommit / GitBranchSwitch, FileChange/FileSource НЕ заполняются (это не файловые события).

### 2. StoppedEngine (src/Continuum.App/Stopped/StoppedEngine.cs, namespace Continuum.Stopped)

Движок «Где я остановился?» - реализация таблицы истинности из readme. Метод:

public async Task<StoppedVerdict> EvaluateAsync(long? sessionId = null, CancellationToken ct = default)

Алгоритм (sessionId null - последняя сессия из GetLastSessionAsync):
1. Сессии нет или в ней нет событий - Unclear (все факты null, DirtyCount=0).
2. last_project: последнее событие сессии с ProjectId != null. Нет такого - Unclear.
3. Проект по GetProjectAsync(projectId). Нет записи - Unclear.
4. last_file: последний FileActivity сессии с этим ProjectId (GetSessionFileActivityAsync). Может отсутствовать - тогда LastFilePath = null, это не Unclear.
5. Живое состояние: _git.GetStateAsync(project.RootPath). Available == false - Unclear, но факты о проекте (Id/Name/Root, LastFilePath) заполняем.
6. Таблица истинности (readme), в таком порядке:
   a. dirty (state.DirtyFiles.Count) > 0 - MaybeUnfinished;
   b. последнее событие проекта (последнее событие сессии с этим ProjectId - из GetSessionEventsAsync) - FileChanged - MaybeUnfinished;
   c. dirty == 0 И последнее событие проекта - GitCommit - Completed;
   d. иначе (чисто, но последнее событие ни правка, ни коммит) - Unclear. Задокументируй этот четвёртый исход комментарием: readme покрывает три ветки, четвёртая - дополнение для тотальности.
7. Заполни StoppedVerdict: Status, ProjectId, ProjectName, ProjectRoot, LastFilePath, Branch, HeadCommit, HeadSubject, HeadTs, DirtyCount.
8. Никогда не бросает: сбой чтения БД или git - Unclear (оберни в try/catch с логом).

Конструктор: (IRepository repository, IGitClient git, ILogger<StoppedEngine> logger).

### 3. Регистрация

В CompositionRoot.cs добавь ОДНУ строку рядом с другими регистрациями шага 4/5: services.AddSingleton<Stopped.StoppedEngine>(); (using Continuum... по месту, как у соседей).

### 4. Тесты

- tests/Continuum.Tests/Collectors/GitHeadDifferTests.cs (или как назовёшь класс): базовый тик молчит; смена ветки - GitBranchSwitch; смена хэша при той же ветке - GitCommit; смена обоих - только GitBranchSwitch; уход в detached - молча; недоступный git не сбрасывает состояние.
- tests/Continuum.Tests/Collectors/ProjectGitMonitorTests.cs: расширь/добавь - на фейковом IGitClient, монитор с малым pollInterval: коммит после базового тика даёт ровно одно событие GitCommit с title "<short7> <subject>" и project root; смена ветки - GitBranchSwitch.
- tests/Continuum.Tests/Stopped/StoppedEngineTests.cs - таблица истинности ЦЕЛИКОМ (критерий шага): нет сессии / нет событий / нет проектных событий / нет записи проекта / git недоступен (Unclear); dirty > 0 (MaybeUnfinished); чисто + последнее событие FileChanged (MaybeUnfinished); чисто + последнее GitCommit (Completed); чисто + последнее другое (Unclear); факты заполнены (project, last file, branch, head). Фейки: FakeRepository (tests/Continuum.Tests/Fakes/FakeRepository.cs - там уже есть Sessions/Events/FileActivities/Projects и нужные read-методы), свой стаб IGitClient.

## Жёсткие ограничения

- НЕ трогай: Continuum.Core/**, readme.md, docs/**, *.csproj, *.sln, CompositionRoot.cs (кроме одной строки регистрации), App.xaml.cs, ObservationPipeline.cs, SessionRuntime.cs, SessionSupervisor.cs, Snapshotter.cs, чужие тесты.
- НЕ делай git-коммитов и git-команд, меняющих состояние репозитория (add/commit/push/reset/checkout). git status/diff - можно. Временные репозитории для тестов - в %TEMP%, удалять за собой.
- Новые NuGet-пакеты запрещены.
- P/Invoke и COM - только в *NativeMethods.cs (здесь, скорее всего, не понадобятся).
- Комментарии и строки - на русском. НИГДЕ не используй длинное тире «-» (U+2014), только дефис «-». Это критично.
- Файлы - UTF-8 без BOM.
- Пространства имён: Continuum.Stopped для движка, Continuum.Collectors для монитора - НЕ Continuum.App.*.
- После работы: dotnet build Continuum.sln (0 ошибок, 0 предупреждений) и dotnet test Continuum.sln (все зелёные) - приложи хвост вывода.

## Отчёт

Перечисли созданные/изменённые файлы с кратким описанием, итог build/test, отступления от задания (если были) и «Замечания к остальному коду» (что заметил чужого - не исправляй, только перечисли).
```

## Что сделал агент

### Файлы созданы

| Файл | Содержимое |
| --- | --- |
| `src/Continuum.App/Collectors/GitHeadDiffer.cs` | Чистый дифф HEAD/ветки: база молчит; коммит - «short7 subject»; смена ветки - «from -> to»; оба сразу - только switch; уход/выход detached молча (задокументировано) |
| `src/Continuum.App/Stopped/StoppedEngine.cs` | Таблица истинности readme + четвёртая ветка (дополнение для тотальности, задокументировано); любой сбой - Unclear без исключения; факты проекта заполняются и при недоступном git |
| `tests/.../GitHeadDifferTests.cs` | 8 тестов |
| `tests/.../ProjectGitMonitorTests.cs` | 4 теста на фейковом IGitClient |
| `tests/.../StoppedEngineTests.cs` | 12 тестов - таблица истинности целиком |

Изменены: `ProjectGitMonitor.cs` (подключение диффера), `CompositionRoot.cs` (ровно одна строка, как задано).

### Проверка

- Агент: build 0/0, тесты 228/228 (было 204 + 24 новых).
- Независимый повторный прогон: build 0/0, 228/228 - сходится. Первый прогон показал 2 красных тайминговых теста (ожидание события 5 с на нагруженной машине) - флаки, не дефект логики: два повторных прогона зелёные, таймауты ожидания подняты до 15 с во всех трёх файлах коллекторных тестов.

## Отступления агента

Не было.

## Поправлено после агента

1. `FakeRepository.GetSessionEventsAsync` - сортировка по времени (замечание агента №3): фейк теперь ведёт себя как Sqlite-реализация.
2. readme: «commit / push» -> «commit» с пояснением - push отдельным событием не отслеживается (замечание агента №1 принято как поправка нормативки, новый EventKind не нужен).
3. Таймауты ожидания в трёх тестовых файлах коллекторов 5 с -> 15 с (флаки на нагруженной машине).
4. Dev-шов `CONTINUUM_EXIT_AFTER_S`: таймер собирался GC до срабатывания (ссылка не удерживалась) - вынесен в поле; без этого живой прогон не завершался сам.

## Замечания агента (ответы)

1. Push не покрыт - принято, readme поправлен (см. выше).
2. Согласованность events/file_activity и ProjectId у GitCommit - подтверждено: пайплайн проставляет ProjectId по ProjectRootPath наблюдения, живой прогон это показал.
3. Сортировка фейка - исправлена (см. выше).

## Живой прогон после интеграции

Временный репозиторий, `CONTINUUM_WATCH_PATH`: правка файла после базового тика -> событие `file_changed`; коммит -> событие `git_commit` с title «7846b86 feat: вторая правка» и project_id. Затем `StoppedEngine` на живой БД (через отладочную обвязку в %TEMP%):

- чистый репозиторий после коммита: **Completed** (branch=main, head, subject, lastFile - все факты заполнены);
- после правки файла без коммита: **MaybeUnfinished**.

Промежуточный Unclear в первом заходе - не дефект движка: обвязке не хватало `SQLitePCL.Batteries_V2.Init()`, движок честно превратил сбой БД в Unclear (именно так и задумано - «никогда не бросает»).

## Что отложено

- UI показа вердикта - шаг 6+ (RestorePlan заодно).
- `git_remote` в `projects` - по-прежнему открыто (нужно для RestorePlan-дедупликации окружений).
