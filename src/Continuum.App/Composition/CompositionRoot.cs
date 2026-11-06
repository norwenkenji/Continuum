using System;
using System.IO;
using Continuum.Core.Abstractions;
using Continuum.Core.Interfaces;
using Continuum.Core.Time;
using Continuum.Infrastructure.Database;
using Continuum.Infrastructure.Git;
using Continuum.Infrastructure.Paths;
using Continuum.Infrastructure.Privacy;
using Continuum.Infrastructure.Processes;
using Continuum.Pipeline;
using Continuum.Projects;
using Continuum.Runtime;
using Continuum.Tray;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Continuum.Composition;

/// <summary>
/// Точка композиции приложения: единственное место, где собирается
/// контейнер зависимостей. Окна получают зависимости через конструктор.
/// </summary>
public static class CompositionRoot
{
    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder => builder.AddDebug());

        // Зависимости ядра -> реализации оболочки
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IAppPaths, AppPaths>();

        // Хранилище: %LOCALAPPDATA%\Continuum\continuum.db
        services.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<IAppPaths>();
            return new SqliteConnectionFactory(Path.Combine(paths.DataDirectory, "continuum.db"));
        });
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IRepository, SqliteRepository>();

        // Privacy-конвейер (docs/specs/privacy-pipeline.md)
        services.AddSingleton<IExclusionSet, ExclusionSet>();
        services.AddSingleton<ISanitizer, SecretSanitizer>();
        services.AddSingleton<IPrivacyFilter, PrivacyFilter>();

        // Сессия и пайплайн наблюдений
        services.AddSingleton<SessionRuntime>();
        services.AddSingleton<ISessionContext>(sp => sp.GetRequiredService<SessionRuntime>());
        services.AddSingleton<ObservationPipeline>();

        // Коллекторы (IObservationSource) регистрируются здесь по мере появления:
        // шаг 2 - WindowMonitor (Process/Window collector)
        services.AddSingleton<IObservationSource, Collectors.WindowMonitor>();

        // Проекты и git (шаг 3). Фабрики - из-за необязательных параметров
        // конструкторов (gitExePath, pollInterval), которые DI не подставит
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IGitClient>(sp => new GitClient(sp.GetRequiredService<IProcessRunner>()));
        services.AddSingleton<IProjectResolver, ProjectResolver>();
        services.AddSingleton<IProjectRegistry, ProjectRegistry>();
        services.AddSingleton<IObservationSource>(sp => new Collectors.ProjectGitMonitor(
            sp.GetRequiredService<IProjectRegistry>(),
            sp.GetRequiredService<IGitClient>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<Collectors.ProjectGitMonitor>>()));

        // Коллекторы файловой активности (data-sources §3.1/§3.2)
        services.AddSingleton<IObservationSource>(sp => new Collectors.FileActivityWatcher(
            sp.GetRequiredService<IProjectRegistry>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<Collectors.FileActivityWatcher>>()));
        services.AddSingleton<IObservationSource, Collectors.RecentFilesMonitor>();

        // Снапшоты и полные границы сессии (шаг 4); с шага 7 снапшот включает
        // открытые файлы (IOpenFilesProvider - адаптер VS Code)
        services.AddSingleton<ISnapshotter>(sp => new Snapshots.Snapshotter(
            sp.GetRequiredService<IRepository>(),
            sp.GetRequiredService<ISessionContext>(),
            sp.GetRequiredService<IProjectRegistry>(),
            sp.GetRequiredService<IGitClient>(),
            sp.GetRequiredService<IPrivacyFilter>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<Snapshots.Snapshotter>>(),
            openFiles: sp.GetRequiredService<Adapters.VSCode.VSCodeAdapter>(),
            projectResolver: sp.GetRequiredService<IProjectResolver>()));
        services.AddSingleton<IIdleTimeSource, IdleTimeSource>();
        services.AddSingleton<SessionSupervisor>();
        services.AddSingleton<Stopped.StoppedEngine>();

        // Адаптер VS Code (шаг 7, data-sources §7): события сохранений из
        // User\History + открытые файлы для снапшота. Dev-шов живой проверки:
        // CONTINUUM_VSCODE_ROOTS=<путь>;<путь> - корни вместо дефолтных
        // (Code, Insiders, VSCodium, Cursor в %APPDATA%)
        var vscodeRoots = Environment.GetEnvironmentVariable("CONTINUUM_VSCODE_ROOTS")?
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        services.AddSingleton<Adapters.VSCode.VSCodeAdapter>(sp => new Adapters.VSCode.VSCodeAdapter(
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<Adapters.VSCode.VSCodeAdapter>>(),
            vscodeRoots is { Length: > 0 } ? vscodeRoots : null,
            sp.GetRequiredService<IProjectResolver>()));
        services.AddSingleton<IObservationSource>(sp => sp.GetRequiredService<Adapters.VSCode.VSCodeAdapter>());
        services.AddSingleton<IOpenFilesProvider>(sp => sp.GetRequiredService<Adapters.VSCode.VSCodeAdapter>());

        // Restore Engine (шаг 6): резолвер путей приложений, запуск, план
        services.AddSingleton<Restore.ApplicationPathResolver>();
        services.AddSingleton<IApplicationResolver>(sp => sp.GetRequiredService<Restore.ApplicationPathResolver>());
        services.AddSingleton<IProcessLauncher, Restore.ProcessLauncher>();
        services.AddSingleton<Restore.RestoreService>();
        services.AddTransient<Presentation.RestorePlanViewModel>();

        // Трей
        services.AddSingleton<TrayIcon>();

        // Окна и модели представления (MVVM, шаг 6)
        services.AddSingleton<Presentation.MainViewModel>();
        services.AddTransient<MainWindow>();

        return services.BuildServiceProvider();
    }
}
