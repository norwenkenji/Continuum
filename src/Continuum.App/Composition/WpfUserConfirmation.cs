using System.Windows;
using Continuum.Presentation;

namespace Continuum.Composition;

/// <summary>
/// Подтверждение через MessageBox - для кнопки «Удалить все данные».
/// Единственное место, где модель представления «видит» диалог через шов.
/// </summary>
public sealed class WpfUserConfirmation : IUserConfirmation
{
    public bool Confirm(string message, string title)
        => System.Windows.MessageBox.Show(
               message,
               title,
               MessageBoxButton.YesNo,
               MessageBoxImage.Warning,
               MessageBoxResult.No) == MessageBoxResult.Yes;
}
