using Continuum.Infrastructure.Paths;
using Xunit;

namespace Continuum.Tests;

public class AppPathsTests
{
    [Fact]
    public void DataDirectory_is_under_local_app_data()
    {
        // Arrange: эталонный корень LocalApplicationData
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // Act: получаем каталог данных приложения
        var dataDirectory = new AppPaths().DataDirectory;

        // Assert: путь начинается с LocalApplicationData
        Assert.StartsWith(localAppData, dataDirectory);

        // Assert: имя последнего каталога в пути — Continuum
        var lastDirectoryName = new DirectoryInfo(dataDirectory).Name;
        Assert.Equal("Continuum", lastDirectoryName);
    }
}
