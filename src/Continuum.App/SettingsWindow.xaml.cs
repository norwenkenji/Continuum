using System;
using System.Windows;
using Continuum.Presentation;

namespace Continuum;

/// <summary>
/// Окно настроек: code-behind только про окно (загрузка данных при открытии,
/// проброс события «данные удалены», закрытие). Логика - в SettingsViewModel.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.LoadAsync();
        _viewModel.DataWiped += (_, _) => DataWiped?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Все данные стёрты - главное окно перечитывает состояние.</summary>
    public event EventHandler? DataWiped;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
