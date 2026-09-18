using Continuum.Core.Abstractions;
using Xunit;

namespace Continuum.Tests;

public class ArchitectureTests
{
    [Fact]
    public void Core_has_no_windows_references()
    {
        // Запрещённые префиксы: ядро не должно зависеть от Windows-стеков
        string[] forbiddenPrefixes =
        [
            "PresentationFramework",
            "PresentationCore",
            "WindowsBase",
            "System.Windows.Forms",
            "System.Drawing",
            "Microsoft.Win32.Registry",
            "System.Management",
        ];

        // Берём имена всех сборок, на которые ссылается сборка ядра
        var referencedNames = typeof(IClock).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();

        foreach (var prefix in forbiddenPrefixes)
        {
            // Ни одно имя не должно начинаться с запрещённого префикса
            Assert.DoesNotContain(referencedNames, name => name.StartsWith(prefix, StringComparison.Ordinal));
        }

        // Дублирующая проверка через Assert.False
        var hasForbidden = referencedNames.Any(name =>
            forbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)));
        Assert.False(hasForbidden);
    }
}
