using Continuum.Infrastructure.Database;
using Xunit;

namespace Continuum.Tests.Database;

public class EmbeddedMigrationLoaderTests
{
    [Theory]
    [InlineData("V001__init.sql", 1, "init")]
    [InlineData("v012__add_browser_tabs.sql", 12, "add_browser_tabs")]
    [InlineData("V3__x.sql", 3, "x")]
    public void TryParseFileName_parses_valid_names(string fileName, int expectedVersion, string expectedName)
    {
        var ok = EmbeddedMigrationLoader.TryParseFileName(fileName, out var version, out var name);

        Assert.True(ok);
        Assert.Equal(expectedVersion, version);
        Assert.Equal(expectedName, name);
    }

    [Theory]
    [InlineData("init.sql")]
    [InlineData("V__init.sql")]
    [InlineData("V1_init.sql")]
    [InlineData("V001__init.txt")]
    [InlineData("")]
    public void TryParseFileName_rejects_invalid_names(string fileName)
    {
        Assert.False(EmbeddedMigrationLoader.TryParseFileName(fileName, out _, out _));
    }
}
