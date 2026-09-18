using System;
using System.IO;
using Continuum.Core.Abstractions;

namespace Continuum.Infrastructure.Paths;

/// <summary>
/// Пути хранения данных в пользовательском профиле:
/// %LOCALAPPDATA%\Continuum. Работает без административных прав.
/// </summary>
public sealed class AppPaths : IAppPaths
{
    public string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Continuum");
}
