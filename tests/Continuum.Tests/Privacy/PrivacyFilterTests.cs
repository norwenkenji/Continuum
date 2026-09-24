using Continuum.Core.Abstractions;
using Continuum.Infrastructure.Privacy;
using Xunit;

namespace Continuum.Tests.Privacy;

/// <summary>
/// Тесты фильтра наблюдений. Норматив: docs/specs/privacy-pipeline.md, §4, §5.3, §9.
/// </summary>
public class PrivacyFilterTests
{
    private static Observable MakeObservable(
        ObservableKind kind,
        string? value = null,
        string? applicationName = null,
        string? applicationExePath = null) =>
        new(
            Kind: kind,
            Value: value,
            ProcessId: 4242,
            ApplicationName: applicationName,
            ApplicationExePath: applicationExePath,
            Timestamp: DateTimeOffset.UtcNow,
            ProjectRootPath: null);

    [Theory]
    [InlineData(ObservableKind.WindowTitle)]
    [InlineData(ObservableKind.FilePath)]
    [InlineData(ObservableKind.Url)]
    [InlineData(ObservableKind.CommandLine)]
    [InlineData(ObservableKind.Cwd)]
    public void Excluded_application_drops_every_kind(ObservableKind kind)
    {
        var filter = new PrivacyFilter(new ExclusionSet());
        var observable = MakeObservable(kind, value: "data", applicationName: "1Password");

        Assert.False(filter.Allow(observable));
    }

    [Fact]
    public void Excluded_directory_drops_FilePath_and_Cwd_but_not_WindowTitle()
    {
        var excludedDirectory = Path.Combine(Path.GetTempPath(), "continuum-excluded");
        var filter = new PrivacyFilter(new ExclusionSet(extraDirectories: [excludedDirectory]));
        var inside = Path.Combine(excludedDirectory, "notes.txt");

        Assert.False(filter.Allow(MakeObservable(ObservableKind.FilePath, inside, "code")));
        Assert.False(filter.Allow(MakeObservable(ObservableKind.Cwd, inside, "code")));

        // Заголовок окна — не путь: правило директорий к нему не применяется
        Assert.True(filter.Allow(MakeObservable(ObservableKind.WindowTitle, inside, "code")));

        // Путь снаружи исключённой директории пропускается
        var outside = Path.Combine(Path.GetTempPath(), "continuum-other", "notes.txt");
        Assert.True(filter.Allow(MakeObservable(ObservableKind.FilePath, outside, "code")));
    }

    [Theory]
    [InlineData(".env")]
    [InlineData(".npmrc")]
    [InlineData(".netrc")]
    [InlineData("credentials.json")]
    [InlineData("id_rsa")]
    [InlineData("id_ed25519")]
    [InlineData("server.pem")]
    [InlineData("private.key")]
    [InlineData("cert.pfx")]
    [InlineData("cert.p12")]
    [InlineData("passwords.kdbx")]
    public void Secret_file_artifact_drops_whole_event(string fileName)
    {
        var filter = new PrivacyFilter(new ExclusionSet());
        var path = Path.Combine("C:\\project", fileName);

        Assert.False(filter.Allow(MakeObservable(ObservableKind.FilePath, path, "code")));
    }

    [Fact]
    public void Ordinary_file_is_allowed()
    {
        var filter = new PrivacyFilter(new ExclusionSet());

        Assert.True(filter.Allow(MakeObservable(ObservableKind.FilePath, "C:\\project\\Program.cs", "code")));
    }

    [Fact]
    public void Continuum_itself_is_always_excluded()
    {
        var filter = new PrivacyFilter(new ExclusionSet());

        Assert.False(filter.Allow(MakeObservable(ObservableKind.WindowTitle, "Continuum", "continuum")));
        Assert.False(filter.Allow(MakeObservable(ObservableKind.WindowTitle, "Continuum", "Continuum")));
        Assert.False(filter.Allow(MakeObservable(
            ObservableKind.WindowTitle, "Continuum", applicationName: null,
            applicationExePath: "C:\\Apps\\Continuum\\Continuum.exe")));
    }

    [Fact]
    public void Directory_boundary_is_respected()
    {
        var filter = new PrivacyFilter(new ExclusionSet(extraDirectories: ["C:\\work"]));

        Assert.False(filter.Allow(MakeObservable(ObservableKind.FilePath, "C:\\work\\project\\a.txt", "code")));
        Assert.False(filter.Allow(MakeObservable(ObservableKind.FilePath, "C:\\work", "code")));

        // Граница каталога: похожий префикс без разделителя — НЕ исключение
        Assert.True(filter.Allow(MakeObservable(ObservableKind.FilePath, "C:\\workspace-secret\\a.txt", "code")));
    }

    [Fact]
    public void Plain_observable_is_allowed()
    {
        var filter = new PrivacyFilter(new ExclusionSet());

        Assert.True(filter.Allow(MakeObservable(ObservableKind.WindowTitle, "Continuum — MainWindow", "code")));
    }
}
