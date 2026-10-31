using System.ComponentModel;
using System.Windows;
using Continuum.Presentation;

namespace Continuum
{
    /// <summary>
    /// Главное окно (MVVM: только привязка DataContext и поведение окна -
    /// трей, скрытие крестиком). Данные и команды - в MainViewModel,
    /// биндинги - в XAML. Настоящий выход только через AllowClose()
    /// (меню трея «Выход»).
    /// </summary>
    public partial class MainWindow : Window
    {
        private bool _allowClose;

        /// <param name="viewModel">Модель представления; injectится из CompositionRoot.</param>
        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            _ = viewModel.RefreshAsync();
        }

        /// <summary>Разрешает настоящее закрытие (выход из приложения).</summary>
        public void AllowClose() => _allowClose = true;

        /// <summary>Возвращает окно из трея.</summary>
        public void ShowFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            base.OnClosing(e);
        }
    }
}
