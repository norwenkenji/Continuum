namespace Continuum.Core.Abstractions;

/// <summary>
/// Пути хранения данных приложения. Реализация живёт в оболочке,
/// чтобы ядро не зависело от Environment и конкретной ОС.
/// </summary>
public interface IAppPaths
{
    /// <summary>Корневой каталог данных: %LOCALAPPDATA%\Continuum.</summary>
    string DataDirectory { get; }
}
