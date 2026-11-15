using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Continuum.Demo;

/// <summary>
/// Генератор демо-данных (шаг 8, readme «Демо-сценарий»): заполняет БД одной
/// синтетической, но реалистичной прошлой сессией и создаёт настоящий
/// DemoProject на диске. Полный цикл «Хронология - Где я остановился? -
/// RestorePlan» показывается на машине без VS Code и git: файлы демо-проекта
/// существуют, поэтому RestorePlan строится и выполняется через OpenFile,
/// а git-состояние честно недоступно (available = 0, вердикт «Статус неясен» -
/// часть демонстрации честной деградации).
/// Запускается приложением в режиме --demo до показа окна.
/// Идемпотентен: ключ настроек demo_seeded; после успешного посева повторный
/// вызов - false без записи. Сбой - false с логом, ключ не ставится.
/// Никогда не бросает.
/// </summary>
public sealed class DemoSeeder
{
    private const string SeededSettingKey = "demo_seeded";
    private const string ProjectName = "DemoProject";
    private const string NotepadName = "Блокнот";
    private const string ExplorerName = "Проводник";

    /// <summary>Хронометраж демо-сессии: 45 событий ровно (задокументировано по требованию шага 8).</summary>
    private const int TimelineEventCount = 45;

    private readonly IRepository _repository;
    private readonly IClock _clock;
    private readonly ILogger<DemoSeeder> _logger;
    private readonly string _demoRoot;

    /// <summary>Приложение демо-сессии: маркер в спеке события, до вставки id.</summary>
    private enum DemoApp
    {
        None,
        Notepad,
        Explorer,
    }

    /// <summary>Спека события таймлайна: смещение в минутах от старта сессии.</summary>
    private sealed record DemoEventSpec(
        EventKind Kind,
        double OffsetMinutes,
        DemoApp App = DemoApp.None,
        string? Title = null,
        string? File = null);

    /// <summary>Спека строки file_activity поверх тех же файлов.</summary>
    private sealed record DemoActivitySpec(
        double OffsetMinutes,
        string File,
        FileChangeKind ChangeKind,
        FileActivitySource Source);

    // Таймлайн: 45 событий, хронологически, в диапазоне [started; ended].
    // Состав: session_start 1, app_started 2, app_focused 18, file_changed 17,
    // git_commit 2, system_sleep 1, system_wake 1, app_exited 2, session_end 1.
    // (Правка README идёт до первого коммита, правки src - до второго; сон
    // и пробуждение - «отошёл» ближе к концу; закрытие приложений - перед
    // концом сессии.)
    private static readonly DemoEventSpec[] Timeline =
    [
        new(EventKind.SessionStart, 0.0),
        new(EventKind.AppStarted, 0.3, DemoApp.Notepad, "README.md - Блокнот"),
        new(EventKind.AppStarted, 0.5, DemoApp.Explorer, "DemoProject"),
        new(EventKind.AppFocused, 1.0, DemoApp.Notepad, "README.md - Блокнот"),
        new(EventKind.FileChanged, 3.0, File: "README.md"),
        new(EventKind.AppFocused, 5.0, DemoApp.Notepad, "README.md - Блокнот"),
        new(EventKind.FileChanged, 7.0, File: "README.md"),
        new(EventKind.AppFocused, 9.0, DemoApp.Explorer, "DemoProject"),
        new(EventKind.FileChanged, 11.0, File: @"src\Program.cs"),
        new(EventKind.AppFocused, 13.0, DemoApp.Notepad, "Program.cs - Блокнот"),
        new(EventKind.FileChanged, 15.0, File: @"src\Program.cs"),
        new(EventKind.FileChanged, 17.0, File: "README.md"),
        new(EventKind.AppFocused, 19.0, DemoApp.Notepad, "README.md - Блокнот"),
        new(EventKind.FileChanged, 21.0, File: @"src\Program.cs"),
        new(EventKind.AppFocused, 23.0, DemoApp.Notepad, "Program.cs - Блокнот"),
        new(EventKind.FileChanged, 25.0, File: @"src\Main.cs"),
        new(EventKind.AppFocused, 27.0, DemoApp.Notepad, "Main.cs - Блокнот"),
        new(EventKind.FileChanged, 29.0, File: @"src\Main.cs"),
        new(EventKind.AppFocused, 31.0, DemoApp.Explorer, "DemoProject"),
        new(EventKind.FileChanged, 33.0, File: @"src\Program.cs"),
        new(EventKind.AppFocused, 35.0, DemoApp.Notepad, "Continuum - Блокнот"),
        new(EventKind.FileChanged, 37.0, File: "README.md"),
        new(EventKind.AppFocused, 39.0, DemoApp.Notepad, "README.md - Блокнот"),
        new(EventKind.FileChanged, 41.0, File: @"src\Main.cs"),
        new(EventKind.AppFocused, 43.0, DemoApp.Notepad, "Main.cs - Блокнот"),
        new(EventKind.FileChanged, 45.0, File: @"src\Program.cs"),
        new(EventKind.AppFocused, 47.0, DemoApp.Notepad, "Program.cs - Блокнот"),
        new(EventKind.FileChanged, 49.0, File: "README.md"),
        new(EventKind.AppFocused, 51.0, DemoApp.Explorer, "DemoProject"),
        new(EventKind.FileChanged, 53.0, File: @"src\Program.cs"),
        new(EventKind.AppFocused, 55.0, DemoApp.Notepad, "README.md - Блокнот"),
        new(EventKind.GitCommit, 56.5, Title: "a1b2c3d демо: правки в README"),
        new(EventKind.AppFocused, 58.0, DemoApp.Notepad, "Program.cs - Блокнот"),
        new(EventKind.FileChanged, 60.0, File: @"src\Main.cs"),
        new(EventKind.AppFocused, 62.0, DemoApp.Notepad, "Main.cs - Блокнот"),
        new(EventKind.AppFocused, 66.0, DemoApp.Notepad, "Continuum - Блокнот"),
        new(EventKind.FileChanged, 70.0, File: @"src\Main.cs"),
        new(EventKind.SystemSleep, 72.0),
        new(EventKind.SystemWake, 78.0),
        new(EventKind.AppFocused, 80.0, DemoApp.Notepad, "Program.cs - Блокнот"),
        new(EventKind.FileChanged, 82.0, File: @"src\Program.cs"),
        new(EventKind.GitCommit, 84.0, Title: "e4f5g6h демо: структура проекта"),
        new(EventKind.AppExited, 86.0, DemoApp.Explorer),
        new(EventKind.AppExited, 88.0, DemoApp.Notepad),
        new(EventKind.SessionEnd, 94.9),
    ];

    // file_activity: 17 строк watcher (по одной на каждое событие file_changed,
    // с тем же Ts) + 3 строки recent (документы открывались в начале и в конце)
    private static readonly DemoActivitySpec[] Activities =
    [
        new(3.0, "README.md", FileChangeKind.Modified, FileActivitySource.Watcher),
        new(7.0, "README.md", FileChangeKind.Modified, FileActivitySource.Watcher),
        new(11.0, @"src\Program.cs", FileChangeKind.Modified, FileActivitySource.Watcher),
        new(15.0, @"src\Program.cs", FileChangeKind.Modified, FileActivitySource.Watcher),
        new(17.0, "README.md", FileChangeKind.Modified, FileActivitySource.Watcher),
        new(21.0, @"src\Program.cs", FileChangeKind.Modified, FileActivitySource.Watcher),
        new(25.0, @"src\Main.cs", FileChangeKind.Modified, FileActivitySource.Watcher),
        new(29.0, @"src\Main.cs", FileChangeKind.Modified, FileActivitySource.Watcher),
        new(33.0, @"src\Program.cs", FileChangeKind.Modified, FileActivitySource.Watcher),
        new(37.0, "README.md", FileChangeKind.Modified, FileActivitySource.Watcher),
        new(41.0, @"src\Main.cs", FileChangeKind.Modified, FileActivitySource.Watcher),
        new(45.0, @"src\Program.cs", FileChangeKind.Modified, FileActivitySource.Watcher),
        new(49.0, "README.md", FileChangeKind.Modified, FileActivitySource.Watcher),
        new(53.0, @"src\Program.cs", FileChangeKind.Modified, FileActivitySource.Watcher),
        new(60.0, @"src\Main.cs", FileChangeKind.Modified, FileActivitySource.Watcher),
        new(70.0, @"src\Main.cs", FileChangeKind.Modified, FileActivitySource.Watcher),
        new(82.0, @"src\Program.cs", FileChangeKind.Modified, FileActivitySource.Watcher),
        new(1.5, "README.md", FileChangeKind.Opened, FileActivitySource.Recent),
        new(12.0, @"src\Program.cs", FileChangeKind.Opened, FileActivitySource.Recent),
        new(50.0, "README.md", FileChangeKind.Opened, FileActivitySource.Recent),
    ];

    public DemoSeeder(
        IRepository repository,
        IClock clock,
        ILogger<DemoSeeder> logger,
        string? demoRoot = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _repository = repository;
        _clock = clock;
        _logger = logger;
        _demoRoot = demoRoot ?? DefaultDemoRoot();
    }

    /// <summary>Демо-проект по умолчанию: %LOCALAPPDATA%\Continuum\DemoProject (readme: данные рядом с БД).</summary>
    private static string DefaultDemoRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Continuum", "DemoProject");
    }

    /// <summary>
    /// Засеивает демо-данные. true - записаны; false - демо уже засеяно
    /// (ключ demo_seeded) или сбой. Любой сбой: лог, ключ НЕ ставится,
    /// исключения наружу не выходят.
    /// </summary>
    public async Task<bool> SeedAsync(CancellationToken ct = default)
    {
        try
        {
            var seeded = await _repository.GetSettingAsync(SeededSettingKey, ct).ConfigureAwait(false);
            if (seeded == "1")
            {
                _logger.LogDebug("Демо-данные уже засеяны, повторный посев пропущен");
                return false;
            }

            var now = _clock.UtcNow;
            var started = now - TimeSpan.FromHours(2);
            var ended = now - TimeSpan.FromMinutes(25);

            // 2. Файлы демо-проекта на диске: только отсутствующие,
            // существующие не перезаписываются (пользователь мог править)
            CreateProjectFiles();
            ct.ThrowIfCancellationRequested();

            // 3. Проект: корень нормализован по контракту IRepository
            var projectId = await _repository.UpsertProjectAsync(new Project(
                Id: 0,
                Name: ProjectName,
                RootPath: NormalizeRoot(_demoRoot),
                GitRemote: null,
                FirstSeen: started,
                LastSeen: ended), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // 4. Одна прошлая сессия: вечер работы, завершена пользователем.
            // CreateSessionAsync по контракту создаёт сессию в статусе running
            // (границы сессии задаёт жизненный цикл), поэтому конец сессии
            // проставляется отдельным вызовом - иначе демо-сессия останется
            // открытой и хронология покажет «идёт»
            var sessionId = await _repository.CreateSessionAsync(new Session(
                Id: 0,
                StartedAt: started,
                EndedAt: null,
                Status: SessionStatus.Running,
                EndReason: null,
                IdleThresholdSeconds: 900), ct).ConfigureAwait(false);
            await _repository.EndSessionAsync(sessionId, ended, SessionEndReason.User, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // 5. Справочник приложений
            var notepadId = await _repository.UpsertApplicationAsync(new ApplicationInfo(
                Id: 0, Name: NotepadName, ExePath: @"C:\Windows\notepad.exe", AdapterKey: null,
                FirstSeen: started, LastSeen: ended), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var explorerId = await _repository.UpsertApplicationAsync(new ApplicationInfo(
                Id: 0, Name: ExplorerName, ExePath: @"C:\Windows\explorer.exe", AdapterKey: null,
                FirstSeen: started, LastSeen: ended), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // 6. События хронологии: 45 штук, Ts - фиксированные смещения
            foreach (var spec in Timeline)
            {
                await _repository.AppendEventAsync(
                    BuildEvent(spec, sessionId, projectId, notepadId, explorerId, started),
                    ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();

            // 7. Активность файлов: те же Ts, пути на существующие файлы
            foreach (var spec in Activities)
            {
                await _repository.AppendFileActivityAsync(new FileActivity(
                    Id: 0,
                    SessionId: sessionId,
                    Ts: started + TimeSpan.FromMinutes(spec.OffsetMinutes),
                    ProjectId: projectId,
                    Path: AbsolutePath(spec.File),
                    ChangeKind: spec.ChangeKind,
                    Source: spec.Source), ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();

            // 8. Снапшот на конец сессии: git честно недоступен (available=0),
            // открытые файлы с позициями - как их видел бы адаптер VS Code
            var snapshot = new Snapshot(
                Id: 0,
                SessionId: sessionId,
                Ts: ended,
                Reason: SnapshotReason.SessionEnd,
                SchemaVersion: 1,
                SummaryJson: "{\"projects\":1,\"gitAvailable\":0,\"dirtyTotal\":0}")
            {
                GitStates =
                [
                    new GitState(
                        Id: 0, SnapshotId: 0, ProjectId: projectId, Available: false,
                        Branch: null, HeadCommit: null, HeadSubject: null, HeadTs: null,
                        DirtyCount: 0, DirtyFilesJson: null, UntrackedCount: 0),
                ],
                OpenFiles =
                [
                    new OpenFile(
                        Id: 0, SnapshotId: 0, Path: AbsolutePath("README.md"),
                        ProjectId: projectId, ApplicationId: null,
                        Line: 42, LastModified: ended, Source: "vscode_state"),
                    new OpenFile(
                        Id: 0, SnapshotId: 0, Path: AbsolutePath(@"src\Program.cs"),
                        ProjectId: projectId, ApplicationId: null,
                        Line: 10, LastModified: ended, Source: "vscode_state"),
                ],
            };
            await _repository.SaveSnapshotAsync(snapshot, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // 9. Восстанавливаемость: пути file_activity и open_files указывают
            // на существующие файлы - RestorePlan построится и выполнится (OpenFile)

            // Посев завершён - только теперь фиксируется ключ идемпотентности
            await _repository.SetSettingAsync(SeededSettingKey, "1", ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Демо-данные засеяны: сессия {SessionId}, {EventCount} событий, проект {Root}",
                sessionId, TimelineEventCount, _demoRoot);
            return true;
        }
        catch (OperationCanceledException)
        {
            // Отмена - не ошибка посева, но данные не завершены: ключ не ставится
            _logger.LogDebug("Посев демо-данных отменён, ключ демо не поставлен");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось засеять демо-данные, ключ демо не поставлен");
            return false;
        }
    }

    /// <summary>Файлы DemoProject: создаются только отсутствующие, существующие не трогаются.</summary>
    private void CreateProjectFiles()
    {
        WriteIfAbsent(Path.Combine(_demoRoot, "README.md"), ReadmeContent);
        WriteIfAbsent(Path.Combine(_demoRoot, "src", "Program.cs"), ProgramContent);
        WriteIfAbsent(Path.Combine(_demoRoot, "src", "Main.cs"), MainContent);
        WriteIfAbsent(Path.Combine(_demoRoot, "project.config"), ConfigContent);
    }

    private static void WriteIfAbsent(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, content);
        }
    }

    /// <summary>Событие из спеки: смещение от старта + привязка к приложению и проекту.</summary>
    private Event BuildEvent(
        DemoEventSpec spec,
        long sessionId,
        long projectId,
        long notepadId,
        long explorerId,
        DateTimeOffset started)
    {
        long? applicationId = spec.App switch
        {
            DemoApp.Notepad => notepadId,
            DemoApp.Explorer => explorerId,
            _ => null,
        };
        var isProjectEvent = spec.Kind is EventKind.FileChanged or EventKind.GitCommit;
        return new Event(
            Id: 0,
            SessionId: sessionId,
            Ts: started + TimeSpan.FromMinutes(spec.OffsetMinutes),
            Kind: spec.Kind,
            ApplicationId: applicationId,
            ProjectId: isProjectEvent ? projectId : null,
            Title: spec.Title,
            Path: spec.File is null ? null : AbsolutePath(spec.File),
            Url: null,
            MetaJson: null);
    }

    private string AbsolutePath(string relative) => Path.Combine(_demoRoot, relative);

    /// <summary>Нормализация корня по контракту IRepository: полный путь, без хвостового разделителя, нижний регистр.</summary>
    private static string NormalizeRoot(string root) =>
        Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();

    private const string ReadmeContent = """
        # DemoProject - демо-проект Continuum

        Эта папка создана режимом --demo: Continuum записал синтетическую,
        но реалистичную сессию работы над этим проектом.

        Что здесь происходит:
        - хронология показывает вечер работы: правки, коммиты, перерыв;
        - «Где я остановился?» указывает на этот проект и последний файл;
        - RestorePlan открывает файлы этой папки без VS Code и git.

        Файлы проекта:
        - src/Program.cs - точка входа демо-программы;
        - src/Main.cs - чтение параметров;
        - project.config - настройки запуска в формате ключ=значение.

        git-репозитория здесь нет намеренно: git-состояние в снапшоте честно
        недоступно (available = 0), и вердикт показывает «Статус неясен» -
        это часть демонстрации честной деградации Continuum.
        """;

    private const string ProgramContent = """
        // DemoProject: точка входа демо-программы.
        // Проект синтетический: код не собирается, но имитирует настоящий,
        // чтобы восстановление контекста выглядело живым.
        namespace DemoProject;

        public static class Program
        {
            public static void Main(string[] args)
            {
                Console.WriteLine("Continuum: демо-проект запущен.");
                var config = Config.Load("project.config");
                var greeting = config.Get("greeting");
                Console.WriteLine(greeting);
                Console.WriteLine($"Аргументов командной строки: {args.Length}");
            }
        }
        """;

    private const string MainContent = """
        // DemoProject: чтение project.config в формате ключ=значение.
        namespace DemoProject;

        public sealed class Config
        {
            private readonly Dictionary<string, string> _values = new()
            {
                ["name"] = "DemoProject",
                ["greeting"] = "Привет из DemoProject",
            };

            public static Config Load(string path) => new();

            public string Get(string key) =>
                _values.TryGetValue(key, out var value) ? value : string.Empty;
        }
        """;

    private const string ConfigContent = """
        name=DemoProject
        mode=demo
        greeting=Привет из DemoProject
        line=10
        """;
}
