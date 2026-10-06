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

        // Трей
        services.AddSingleton<TrayIcon>();

        // Окна
        services.AddTransient<MainWindow>();

        return services.BuildServiceProvider();
    }
}
