namespace Continuum.Core.Abstractions;

/// <summary>
/// Источник «длительности бездействия пользователя» (GetLastInputInfo).
/// Шов для тестов: SessionSupervisor проверяется на фейке без реального ввода.
/// Реализация живёт в оболочке (Runtime/IdleTimeSource).
/// </summary>
public interface IIdleTimeSource
{
    /// <summary>Сколько времени система не получала ввода (мышь/клавиатура).</summary>
    TimeSpan GetIdleTime();
}
