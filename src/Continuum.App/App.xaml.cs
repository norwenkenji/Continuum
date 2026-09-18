using System.Windows;
using Continuum.Composition;
using Microsoft.Extensions.DependencyInjection;

namespace Continuum
{
    /// <summary>
    /// Точка входа WPF-приложения. Вся логика запуска — сборка контейнера
    /// зависимостей и показ главного окна; бизнес-логики здесь нет.
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            ServiceProvider services = CompositionRoot.Build();
            MainWindow window = services.GetRequiredService<MainWindow>();
            window.Show();
        }
    }
}
