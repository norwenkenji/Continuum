namespace Continuum.Presentation;

/// <summary>
/// Подтверждение опасного действия (полное удаление данных). Шов, чтобы
/// модель представления не знала про MessageBox: в тестах подставляется
/// заглушка, в приложении - WPF-реализация.
/// </summary>
public interface IUserConfirmation
{
    /// <summary>True - пользователь подтвердил действие.</summary>
    bool Confirm(string message, string title);
}
