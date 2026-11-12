namespace Continuum.Core.Abstractions;

/// <summary>
/// Пути хранения данных приложения. Реализация живёт в оболочке,
/// чтобы ядро не зависело от Environment и конкретной ОС.
/// </summary>
public interface IAppPaths
{
    /// <summary>Корневой каталог данных: %LOCALAPPDATA%\Continuum.</summary>
    string DataDirectory { get; }

    /// <summary>
    /// Основная база данных: %LOCALAPPDATA%\Continuum\continuum.db.
    /// </summary>
    string DatabasePath { get; }

    /// <summary>
    /// Демонстрационная база (--demo, readme «Демо-режим»): continuum.demo.db -
    /// отдельная, чтобы демо-данные не смешивались с реальными.
    /// </summary>
    string DemoDatabasePath { get; }
}
