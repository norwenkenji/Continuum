using System;
using System.Windows;
using Continuum.Composition;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Infrastructure.Database;
using Continuum.Pipeline;
using Continuum.Runtime;
using Continuum.Tray;
using Microsoft.Extensions.DependencyInjection;

namespace Continuum
{
    /// <summary>
    /// Точка входа WPF-приложения. Запуск: контейнер зависимостей → миграции БД
    /// → старт сессии → подключение коллекторов к privacy-конвейеру → трей → окно.
    /// Бизнес-логики здесь нет.
    /// (Application и MessageBox квалифицированы: UseWindowsForms даёт одноимённые
    /// типы в System.Windows.Forms.)
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private ServiceProvider? _services;
        private TrayIcon? _tray;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            ServiceProvider services = CompositionRoot.Build();
            _services = services;

            try
            {
                // Миграции - до первого окна и до первой записи
                services.GetRequiredService<DatabaseInitializer>().Initialize();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Не удалось инициализировать базу данных.\n\n{ex.Message}",
                    "Continuum - ошибка запуска",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
                return;
            }

            // Сессия - до старта коллекторов: их события пишутся в неё
            services.GetRequiredService<SessionRuntime>().StartAsync().GetAwaiter().GetResult();

            // Dev-шов живой проверки: CONTINUUM_WATCH_PATH=<путь> регистрирует
            // проект в реестре до старта коллекторов (пользовательского UI
            // регистрации проектов в MVP нет - пути придут из шагов 7/10)
            var watchPath = Environment.GetEnvironmentVariable("CONTINUUM_WATCH_PATH");
            if (!string.IsNullOrWhiteSpace(watchPath))
            {
                services.GetRequiredService<IProjectRegistry>()
                    .RegisterPathAsync(watchPath).GetAwaiter().GetResult();
            }

            var pipeline = services.GetRequiredService<ObservationPipeline>();
            foreach (var source in services.GetServices<IObservationSource>())
            {
                pipeline.Attach(source);
                source.Start();
            }

            _tray = services.GetRequiredService<TrayIcon>();

            MainWindow window = services.GetRequiredService<MainWindow>();
            _tray.OpenRequested += (_, _) => window.ShowFromTray();
            _tray.ExitRequested += (_, _) =>
            {
                window.AllowClose();
                Shutdown(); // завершение сессии - в OnExit
            };

            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                // Один путь завершения на все случаи: трей, меню, explicit Shutdown
                _services?.GetRequiredService<SessionRuntime>()
                    .StopAsync(SessionEndReason.User).GetAwaiter().GetResult();
                _services?.GetRequiredService<ObservationPipeline>().DetachAll();
            }
            catch
            {
                // При выходе не падаем: сессия закроется как crash_recovered
                // при следующем старте
            }

            _tray?.Dispose();
            _services?.Dispose();
            base.OnExit(e);
        }
    }
}
