# 2026-10-06 - Шаг 6: RestorePlan + MVVM (агент)

## Задача

Шаг 6 дорожной карты (readme): RestorePlan. Критерий: «Падение одного шага не отменяет остальные; dry-run ничего не запускает; повтор не плодит дубликаты». Плюс требование курсовой из письма с заданием: паттерн MVVM (в `plan.md` §26 зафиксировано решение вводить его на этом шаге). Плюс отложенный с шага 5 UI-показ вердикта «Где я остановился?».

Разделение: швы (контракты `IApplicationResolver`/`IProcessLauncher`, read-методы репозитория для restore_plans/restore_steps, MVVM-базовые классы, `MainViewModel` + MainWindow на биндингах, wiring) - вне делегации; агенту - резолвер путей, `RestoreService`, `RestorePlanViewModel` + тесты.

## Вне делегации (швы)

- `IApplicationResolver` и `IProcessLauncher` в Core/Abstractions: резолв пути приложения по имени (null - не найден, шаг будет failed) и запуск процесса без ожидания (UseShellExecute). IProcessLauncher отдельно от IProcessRunner: тому нужны потоки и ожидание (git), этому - «запусти и забыл».
- `IRepository`: `GetLastSnapshotAsync`, `CreateRestorePlanAsync` (план + шаги одной транзакцией), `SetRestorePlanStatusAsync`, `SetRestoreStepResultAsync`, `GetLastRestorePlanAsync` (+Sqlite с маппингом restore-таблиц, +Fake).
- MVVM-каркас без сторонних пакетов: `ViewModelBase` (INotifyPropertyChanged + SetField), `RelayCommand` (ICommand). Тезис для защиты: ViewModel покрывается теми же юнит-тестами, что движки - MVVM проверяемое свойство, а не декларация.
- `MainViewModel` + MainWindow.xaml на биндингах: сводка последней сессии (длительность, приложений, файлов) и вердикт «Где я остановился?» (закрыт отложенный UI шага 5), команда «Обновить». Code-behind остался только про трей и скрытие крестиком.

## Агент

- **Модель:** GLM-5.3-Prime (Z.ai)
- **Способ:** задача делегирована промптом ниже, отчёт получен и проверен независимым прогоном.

## Промпт (передан агенту)

```text
Контекст: проект Continuum - локальный WPF-рекордер рабочего контекста (.NET 10, WPF + SQLite, local-first, русский UI). Репозиторий: git, ветка main. SDK: dotnet 10.0.401. Сборка сейчас: 0 ошибок, 0 предупреждений, 228 тестов зелёные. Так должно остаться и после тебя.

ОБЯЗАТЕЛЬНО ПРОЧИТАЙ ПЕРЕД РАБОТОЙ:
1. readme.md - разделы «Restore Engine» (порядок шагов значим, идемпотентность, dry-run), «Порядок доведения до MVP» шаг 6, «Бюджет ресурсов».
2. Контракты ядра (уже в дереве, НЕ редактируй):
   - src/Continuum.Core/Abstractions/IApplicationResolver.cs - ResolveExecutable(name) → string? (null - не найден)
   - src/Continuum.Core/Abstractions/IProcessLauncher.cs - TryStart(fileName, arguments?, workingDirectory?) → bool (не бросает)
   - src/Continuum.Core/Domain/RestorePlan.cs - RestorePlan, RestoreStep (Seq - порядок, Result nullable)
   - src/Continuum.Core/Domain/Enums.cs - RestoreStrategyKind, RestorePlanStatus, RestoreStepResult
   - src/Continuum.Core/Interfaces/IRepository.cs - CreateRestorePlanAsync, SetRestorePlanStatusAsync, SetRestoreStepResultAsync, GetLastRestorePlanAsync, GetLastSnapshotAsync, GetSessionEventsAsync, GetSessionFileActivityAsync
3. src/Continuum.App/Presentation/ - MVVM-каркас уже есть (ViewModelBase, RelayCommand, MainViewModel); твоя RestorePlanViewModel подключится к нему.

## Что сделать

### 1. ApplicationPathResolver (src/Continuum.App/Restore/ApplicationPathResolver.cs, namespace Continuum.Restore)

Реализация IApplicationResolver: App Paths из реестра (HKCU → HKLM → Wow6432Node) → ключи Uninstall (DisplayName содержит имя → InstallLocation + типовые имена exe) → известные расположения (%LOCALAPPDATA%\Programs\<name>\<name>.exe, Program Files, Program Files (x86), GitHubDesktop app-*) → PATH (перебор каталогов). Кэш ConcurrentDictionary с отрицательными результатами. Реестр через Microsoft.Win32.Registry (только чтение, не P/Invoke). Реестровые источники за швом IAppPathSource (public - InternalsVisibleTo в проекте нет) со стабами в тестах. Никогда не бросает.

### 2. ProcessLauncher (src/Continuum.App/Restore/ProcessLauncher.cs)

IProcessLauncher: Process.Start(UseShellExecute=true), без ожидания и чтения потоков; исключение → false.

### 3. RestoreService (src/Continuum.App/Restore/RestoreService.cs) - главная часть

public Task<RestorePlan> BuildPlanAsync(long? sessionId = null, IReadOnlyList<RestoreHostApp>? hosts = null, CancellationToken ct = default)
public Task<RestorePlan> ExecutePlanAsync(RestorePlan plan, bool dryRun, CancellationToken ct = default)

RestoreHostApp - record (ProcessName, Arguments?): приложения-хосты передаются извне (в БД нет read-метода приложений - MVP, задокументируй).

BuildPlanAsync: последняя сессия (или заданная); якорь снапшота GetLastSnapshotAsync (нет снапшота - SnapshotId=0, план НЕ выполняется и НЕ пишется: restore_plans.snapshot_id NOT NULL, план - восстановление снапшота, задокументируй); шаги: hosts первыми (StartProcess, target = имя процесса, ArgsJson {"arguments": ...} при наличии), затем файлы из file_activity (distinct по пути, последние 10 от свежего к старому, существующие) - OpenFile. План не пишется в БД - запись делает ExecutePlanAsync.

ExecutePlanAsync: без снапшота или шагов - вернуть как есть; план пишется ДО выполнения (CreateRestorePlanAsync, шаги перечитываются с Id через GetLastRestorePlanAsync); dryRun - все шаги Skipped «dry-run: не запускался», НИ ОДНОГО запуска и резолва, статус Success; выполнение по Seq: StartProcess - идемпотентность через проверку «уже запущен» (шов IRunningProcessCheck, реализация Process.GetProcessesByName - разовая проверка при восстановлении, НЕ цикл мониторинга) → Skipped «уже запущен»; резолв при выполнении (null → Failed «Приложение не найдено: <name>»); TryStart → Success/Failed. OpenFile - File.Exists (нет → Failed «Файл не существует»), TryStart(target). DurationMs по Stopwatch, SetRestoreStepResultAsync после каждого шага, падение шага не отменяет остальные. Итог: все успех → Success, смесь → Partial, все сбой → Failed; SetRestorePlanStatusAsync со временами. Никогда не бросает.

### 4. RestorePlanViewModel (src/Continuum.App/Presentation/RestorePlanViewModel.cs, namespace Continuum.Presentation)

MVVM поверх RestoreService: IsBusy, PlanSummary («Шагов: N, успех: X, сбои: Y»), ObservableCollection<RestoreStepViewModel> (Seq, StrategyText, Target, ResultText, Error, ResultKind для индикации); команды BuildCommand/DryRunCommand/ExecuteCommand (async void с try/catch, canExecute: !IsBusy, await без ConfigureAwait(false) - обновления возвращаются на поток UI).

### 5. Регистрация в CompositionRoot.cs (5 строк рядом со StoppedEngine)

ApplicationPathResolver + IApplicationResolver (пара), IProcessLauncher → ProcessLauncher, RestoreService, Transient RestorePlanViewModel.

### 6. Тесты

- Restore/ApplicationPathResolverTests.cs: App Paths найден (Uninstall не опрашивается), битые значения пропускаются, приоритет HKCU→HKLM, кавычки снимаются, не найдено → null, кэш положительный/отрицательный (счётчики стаба), нормализация .exe, Uninstall-матч по DisplayName, PATH-перебор последним, пустое имя → null.
- Restore/RestoreServiceTests.cs (FakeRepository + стабы IApplicationResolver/IProcessLauncher/IClock/IRunningProcessCheck): пустой план без сессий; distinct/последние-10/существование/порядок файлов; хосты первыми; SnapshotId=0; невыполнение без снапшота/шагов; dry-run (0 запусков, 0 резолвов, все Skipped, запись в БД); успех файлового шага (+персистентность); «Файл не существует»; «Приложение не найдено»; сбой лаунчера; падение первого шага не отменяет второй (Partial); «уже запущен»; статусы плана.
- Presentation/RestorePlanViewModelTests.cs: BuildCommand заполняет шаги; DryRun - «Пропущен», 0 запусков; Execute - «Успех»; сбой - «Сбой»; пустые данные не роняют UI.

## Жёсткие ограничения

- НЕ трогай: Continuum.Core/**, readme.md, docs/**, *.csproj, *.sln, App.xaml.cs, MainWindow.xaml*, CompositionRoot.cs (кроме 5 строк), SessionRuntime.cs, SessionSupervisor.cs, Snapshotter.cs, ObservationPipeline.cs, StoppedEngine.cs, MainViewModel.cs, чужие тесты.
- НЕ делай git-коммитов и git-команд, меняющих состояние репозитория. НИЧЕГО не запускай из тестов (IProcessLauncher - стаб).
- Новые NuGet-пакеты запрещены. P/Invoke/COM - только в *NativeMethods.cs (не нужны).
- Комментарии и строки - на русском. НИГДЕ не используй длинное тире «-» (U+2014), только дефис «-». Это критично.
- Файлы - UTF-8 без BOM. Без CJK-символов в коде.
- Пространства имён: Continuum.Restore, Continuum.Presentation - НЕ Continuum.App.*.
- После работы: dotnet build (0/0) и dotnet test (все зелёные) - приложи хвост вывода.

## Отчёт

Перечисли созданные/изменённые файлы с кратким описанием, итог build/test, отступления и «Замечания к остальному коду».
```

## Что сделал агент

### Файлы созданы

| Файл | Содержимое |
| --- | --- |
| `src/Continuum.App/Restore/ApplicationPathResolver.cs` | Резолвер App Paths → Uninstall → известные → PATH; кэш с отрицательными результатами; шов IAppPathSource (public) с реальной RegistryAppPathSource; GitHubDesktop свежайший app-* |
| `src/Continuum.App/Restore/ProcessLauncher.cs` | IProcessLauncher: UseShellExecute, исключение → false |
| `src/Continuum.App/Restore/RestoreService.cs` | Build/Execute по заданию; RestoreHostApp, шов IRunningProcessCheck + RunningProcessCheck; ArgsJson {"arguments"}; ComputePlanStatus; никогда не бросает |
| `src/Continuum.App/Presentation/RestorePlanViewModel.cs` | MVVM: IsBusy/PlanSummary/Steps + 3 команды; RestoreStepViewModel + RestoreStepResultKind |
| `tests/.../ApplicationPathResolverTests.cs` | 12 тестов |
| `tests/.../RestoreServiceTests.cs` | 13 тестов |
| `tests/.../RestorePlanViewModelTests.cs` | 5 тестов |

Изменены: `CompositionRoot.cs` (ровно 5 строк), `ProjectGitMonitorTests.cs` (флака МОЕГО же теста шага 5: гонка базового тика под параллельной нагрузкой, теперь база ждётся детерминированно по счётчику вызовов фейка).

### Проверка

- Агент: build 0/0, тесты 258/258 (228 + 30), два прогона зелёные.
- Независимо: build 0/0; серия из 6 полных прогонов после моей поправки - 6/6 зелёные (до поправки ловился 1 красный на ~каждом пятом, см. ниже).

## Отступления агента (приняты)

1. `GetAppPathEntries(name)` отдаёт список кандидатов в порядке приоритета вместо одного значения - логика приоритета HKCU→HKLM и пропуска битых значений переехала в резолвер и покрыта его тестами. Семантика сохранена.
2. `IAppPathSource` и `UninstallEntry` public - InternalsVisibleTo в проекте нет, стаб из тестовой сборки иначе не собрать.

## Поправлено при интеграции

1. **Флака RecentFilesMonitorTests** (поймана при независимой проверке: `Changed_mtime_emits_again` краснел по 15-секундному таймауту примерно на каждом пятом прогоне). Корень - та же гонка, что агент починил в ProjectGitMonitorTests: `WaitBaseline() = Thread.Sleep(250)`, а под параллельной нагрузкой первый тик монитора опаздывал дольше сна - свежий ярлык попадал в молчаливый базовый снимок, события не было. Исправление в том же духе: резолвер-стаб теста теперь считает вызовы, база ждётся детерминированно по ярлыку-сентинелу (`StartAndWaitBaseline`), фиксированный сон удалён. После правки - 6/6 зелёных полных прогонов.
2. `StoppedEngineTests.ThrowingRepository` - обёртка IRepository не реализовала новые restore-методы интерфейса (red build после добавления швов); добавлены делегирующие реализации.

## Замечания агента (ответы)

1. Read-метода приложений в IRepository нет - так и задумано (MVP): хосты передаются параметром, задел на чтение справочника - после MVP.
2. «readme упоминает push» - устарело: «commit / push» вычищен ещё на шаге 5, grep пуст (push оставался только в gitignored plan.md, там и лежит как история замысла).
3. Сортировка FakeRepository - согласовано: события и file_activity по Ts, планы по Id; у планов порядок не влияет на логику (шаги упорядочены по Seq).
4. IsBusy в MainViewModel - принято: у MainViewModel команды read-only, блокировать нечего; при появлении «тяжёлых» команд приведу к единому стилю.

## Живой прогон после интеграции

Временный репозиторий, dev-швы `CONTINUUM_WATCH_PATH` + `CONTINUUM_EXIT_AFTER_S`: сессия с новым файлом (watcher → file_activity) и снапшотом на выходе. Затем RestoreService на живой БД через отладочную обвязку в %TEMP%:

- план: 2 шага - notepad (хост) и notes.txt (из file_activity), snapshotId привязан к снапшоту сессии;
- dry-run: статус Success, оба шага Skipped «dry-run: не запускался», ни одного процесса;
- реальный запуск: notepad - Skipped «уже запущен» (идемпотентность на живом процессе), notes.txt открыт - Success;
- повторный прогон: notepad снова «уже запущен» - дубликатов нет;
- резолвер нашёл notepad через App Paths (WindowsApps-алиас).

UI-часть: MainWindow с биндингами прожил полный цикл приложения (запуск → работа → самовыход по таймеру) без падений.

## Что отложено

- Чтение справочника приложений из БД для шагов-хостов (нужен read-метод + коалесценция exe_path) - после MVP.
- OpenUrl/ExecuteCommand-стратегии (нет данных: URL с шага 10, whitelist-команды по плану §16) - по мере шагов 7-10.
- Timeline-окно (Хронология) - шаг 7-8 вместе с демо.
