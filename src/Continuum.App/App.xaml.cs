using System;
using System.Windows;
using Continuum.Composition;
using Continuum.Infrastructure.Database;
using Microsoft.Extensions.DependencyInjection;

namespace Continuum
{
    /// <summary>
    /// Точка входа WPF-приложения. Вся логика запуска — сборка контейнера
    /// зависимостей, приведение БД к актуальной схеме и показ главного окна;
    /// бизнес-логики здесь нет.
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            ServiceProvider services = CompositionRoot.Build();

            try
            {
                // Миграции — до первого окна и до первой записи
                services.GetRequiredService<DatabaseInitializer>().Initialize();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Не удалось инициализировать базу данных.\n\n{ex.Message}",
                    "Continuum — ошибка запуска",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
                return;
            }

            MainWindow window = services.GetRequiredService<MainWindow>();
            window.Show();
        }
    }
}
