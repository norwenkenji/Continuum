using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Continuum.Presentation;

/// <summary>
/// База моделей представления (MVVM). Требование курсовой: паттерн MVVM,
/// своя инфраструктура без сторонних пакетов (~30 строк - честнее для защиты,
/// чем CommunityToolkit.Mvvm). ViewModel покрывается теми же юнит-тестами,
/// что и движки: зависимости приходят конструктором, UI не нужен.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Уведомляет об изменении свойства; имя берётся из вызова.</summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Устанавливает значение и уведомляет, только если оно изменилось.</summary>
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
