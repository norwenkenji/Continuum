using System.ComponentModel;
using System.Windows;

namespace Continuum
{
    /// <summary>
    /// Главное окно. Закрытие крестиком прячет окно в трей - рекордер
    /// продолжает работать фоном; настоящий выход только через AllowClose()
    /// (меню трея «Выход»).
    /// </summary>
    public partial class MainWindow : Window
    {
        private bool _allowClose;

        public MainWindow()
        {
            InitializeComponent();
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
