using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using Continuum.Presentation;

namespace Continuum
{
    /// <summary>
    /// Главное окно (MVVM: только привязка DataContext и поведение окна -
    /// трей, скрытие крестиком, открытие окна настроек). Данные и команды -
    /// в MainViewModel, биндинги - в XAML. Настоящий выход только через
    /// AllowClose() (меню трея «Выход»).
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly Func<SettingsWindow> _settingsFactory;
        private SettingsWindow? _settingsWindow;
        private bool _allowClose;

        /// <param name="viewModel">Модель представления; injectится из CompositionRoot.</param>
        /// <param name="settingsFactory">Фабрика окна настроек: окно создаётся по требованию.</param>
        public MainWindow(MainViewModel viewModel, Func<SettingsWindow> settingsFactory)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _settingsFactory = settingsFactory ?? throw new ArgumentNullException(nameof(settingsFactory));
            InitializeComponent();
            DataContext = _viewModel;
            _viewModel.SettingsRequested += (_, _) => OpenSettings();
            _ = _viewModel.RefreshAsync();
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

        /// <summary>Открывает окно настроек (повторный вызов активирует открытое).</summary>
        public void OpenSettings()
        {
            if (_settingsWindow is { IsLoaded: true })
            {
                _settingsWindow.Activate();
                return;
            }

            var window = _settingsFactory();
            window.Owner = this;
            window.DataWiped += async (_, _) => await RefreshAfterWipeAsync();
            window.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow = window;
            window.Show();
        }

        private async Task RefreshAfterWipeAsync()
        {
            // База стёрта и создана заново: главное окно перечитывает состояние
            await _viewModel.RefreshAsync();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            // Окно настроек не должно держать приложение после выхода
            _settingsWindow?.Close();
            base.OnClosing(e);
        }
    }
}
