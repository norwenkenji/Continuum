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
        private System.Threading.Timer? _exitTimer;
        private bool _sessionClosedOnShutdown;
        private bool _demoMode;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // --demo (readme «Демо-режим как страховка»): отдельная БД, полный
            // цикл показывается на машине без VS Code и git
            _demoMode = e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase);

            ServiceProvider services = CompositionRoot.Build(_demoMode);
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

            // Dev-шов живой проверки выхода: CONTINUUM_EXIT_AFTER_S=<секунды> -
            // авто-Shutdown через диспетчер (крестик окна прячет в трей,
            // поэтому graceful-выход из скрипта возможен только так).
            // До ветки --demo: работает в обоих режимах
            if (int.TryParse(Environment.GetEnvironmentVariable("CONTINUUM_EXIT_AFTER_S"), out var exitAfterSeconds)
                && exitAfterSeconds > 0)
            {
                // Ссылка в поле: без неё таймер собирается GC до срабатывания
                _exitTimer = new System.Threading.Timer(
                    _ => Dispatcher.InvokeAsync(Shutdown),
                    null,
                    dueTime: exitAfterSeconds * 1000,
                    period: Timeout.Infinite);
            }

            // Настройки (шаг 9): исключения и порог простоя применяются до старта
            // коллекторов - конвейер сразу работает с настоящими исключениями
            services.GetRequiredService<Settings.SettingsService>().LoadAsync().GetAwaiter().GetResult();

            if (_demoMode)
            {
                // Демо-режим: засеянные данные, живые коллекторы и сессия
                // не запускаются - демо-БД остаётся чистой. Идемпотентно:
                // повторный --demo не дублирует сессию (ключ demo_seeded).
                services.GetRequiredService<Demo.DemoSeeder>().SeedAsync().GetAwaiter().GetResult();

                services.GetRequiredService<Presentation.SettingsViewModel>().WipeEnabled = false;
                _tray = services.GetRequiredService<TrayIcon>();
                var demoWindow = services.GetRequiredService<MainWindow>();
                _tray.SettingsRequested += (_, _) =>
                {
                    demoWindow.ShowFromTray();
                    demoWindow.OpenSettings();
                };
                _tray.OpenRequested += (_, _) => demoWindow.ShowFromTray();
                _tray.ExitRequested += (_, _) =>
                {
                    demoWindow.AllowClose();
                    Shutdown();
                };
                demoWindow.Show();
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

            // Срок хранения (ФТ-7.2): проверка при старте и фоном раз в 6 часов
            var retention = services.GetRequiredService<Maintenance.RetentionService>();
            retention.RunOnceAsync().GetAwaiter().GetResult();
            retention.Start();

            // Границы сессии и снапшоты по таймеру (шаг 4)
            var supervisor = services.GetRequiredService<SessionSupervisor>();
            supervisor.Start();
            supervisor.HookPowerEvents();
            ((Snapshots.Snapshotter)services.GetRequiredService<ISnapshotter>()).Start();

            _tray = services.GetRequiredService<TrayIcon>();

            MainWindow window = services.GetRequiredService<MainWindow>();
            _tray.OpenRequested += (_, _) => window.ShowFromTray();
            _tray.SettingsRequested += (_, _) =>
            {
                window.ShowFromTray();
                window.OpenSettings();
            };
            _tray.ExitRequested += (_, _) =>
            {
                window.AllowClose();
                Shutdown(); // завершение сессии - в OnExit
            };

            window.Show();
        }

        /// <summary>
        /// Завершение сеанса Windows (logoff/shutdown): ~5 секунд на синхронную
        /// запись снапшота (readme «Границы сессии»). Сессия закрывается здесь же,
        /// чтобы OnExit не дублировал запись.
        /// </summary>
        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            try
            {
                if (_services is not null && !_demoMode)
                {
                    _services.GetRequiredService<SessionSupervisor>().Stop();
                    _services.GetRequiredService<ISnapshotter>()
                        .SaveSnapshotAsync(SnapshotReason.Shutdown).GetAwaiter().GetResult();
                    _services.GetRequiredService<SessionRuntime>()
                        .StopAsync(SessionEndReason.Shutdown).GetAwaiter().GetResult();
                    _sessionClosedOnShutdown = true;
                }
            }
            catch
            {
                // Отменять завершение сеанса нельзя - просто уходим
            }

            base.OnSessionEnding(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                // В демо-режиме сессии и коллекторов нет - закрывать нечего
                if (_services is not null && !_sessionClosedOnShutdown && !_demoMode)
                {
                    _services.GetRequiredService<SessionSupervisor>().Stop();
                    ((Snapshots.Snapshotter)_services.GetRequiredService<ISnapshotter>()).Stop();

                    // Снапшот на границе сессии - до её закрытия (критерий шага 4)
                    _services.GetRequiredService<ISnapshotter>()
                        .SaveSnapshotAsync(SnapshotReason.SessionEnd).GetAwaiter().GetResult();

                    // Один путь завершения на все случаи: трей, меню, explicit Shutdown
                    _services.GetRequiredService<SessionRuntime>()
                        .StopAsync(SessionEndReason.User).GetAwaiter().GetResult();
                    _services.GetRequiredService<ObservationPipeline>().DetachAll();
                }
            }
            catch
            {
                // При выходе не падаем: сессия закроется как crash_recovered
                // при следующем старте
            }

            _tray?.Dispose();
            _exitTimer?.Dispose();
            _services?.Dispose();
            base.OnExit(e);
        }
    }
}
