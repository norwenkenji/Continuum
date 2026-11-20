namespace Continuum.Core.Abstractions;

/// <summary>
/// Текущий порог простоя для границ сессии (ФТ-3.1: порог настраиваемый).
/// Отдельный шов, чтобы супервизор сессии не зависел от хранилища настроек
/// и читал актуальное значение на каждом тике.
/// </summary>
public interface IIdleThresholdProvider
{
    TimeSpan Current { get; }
}
