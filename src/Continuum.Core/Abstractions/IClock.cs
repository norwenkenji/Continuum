namespace Continuum.Core.Abstractions;

/// <summary>
/// Источник текущего времени. Все компоненты получают время только через
/// этот интерфейс, чтобы тесты могли подменять часы.
/// </summary>
public interface IClock
{
    /// <summary>Текущее время в UTC.</summary>
    DateTimeOffset UtcNow { get; }
}
