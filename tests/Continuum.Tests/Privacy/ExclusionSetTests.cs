using Continuum.Infrastructure.Privacy;
using Xunit;

namespace Continuum.Tests.Privacy;

/// <summary>
/// Тесты набора исключений. Норматив: docs/specs/privacy-pipeline.md, §4.
/// </summary>
public class ExclusionSetTests
{
    [Theory]
    [InlineData("1password")]
    [InlineData("keepass")]
    [InlineData("bitwarden")]
    [InlineData("enpass")]
    [InlineData("dashlane")]
    [InlineData("lastpass")]
    [InlineData("nordpass")]
    [InlineData("stickypasswords")]
    [InlineData("keeper")]
    [InlineData("continuum")]
    public void Default_applications_are_excluded(string applicationName)
    {
        var set = new ExclusionSet();

        Assert.True(set.IsApplicationExcluded(applicationName, null));
    }

    [Fact]
    public void Application_comparison_is_case_insensitive_and_ignores_extension()
    {
        var set = new ExclusionSet();

        Assert.True(set.IsApplicationExcluded("KEEPASS", null));
        Assert.True(set.IsApplicationExcluded("KeePass.exe", null));
        Assert.True(set.IsApplicationExcluded("BitWarden.EXE", null));

        Assert.False(set.IsApplicationExcluded("Code", null));
        Assert.False(set.IsApplicationExcluded("notepad", null));
    }

    [Fact]
    public void Exe_path_matches_by_file_name_and_by_full_path()
    {
        var set = new ExclusionSet();

        // По имени файла exe, даже если applicationName другое или пустое
        Assert.True(set.IsApplicationExcluded("whatever", "C:\\Program Files\\KeePass Password Safe 2\\KeePass.exe"));
        Assert.True(set.IsApplicationExcluded("", "C:\\Apps\\1Password\\1Password.exe"));

        Assert.False(set.IsApplicationExcluded("whatever", "C:\\Program Files\\VS Code\\Code.exe"));
    }

    [Fact]
    public void Extra_application_by_full_path_matches_only_that_path()
    {
        var set = new ExclusionSet(extraApplications: ["C:\\Tools\\SecretTool\\SecretTool.exe"]);

        // Тот самый путь — исключён
        Assert.True(set.IsApplicationExcluded("anything", "C:\\Tools\\SecretTool\\SecretTool.exe"));
        // Сравнение регистронезависимое
        Assert.True(set.IsApplicationExcluded("anything", "c:\\tools\\secrettool\\secrettool.exe"));

        // Имя само по себе и тот же exe в другом каталоге — не исключены:
        // «путь важнее имени» (§4.1)
        Assert.False(set.IsApplicationExcluded("SecretTool", null));
        Assert.False(set.IsApplicationExcluded("anything", "C:\\Other\\SecretTool.exe"));
    }

    [Fact]
    public void Default_directories_are_excluded_with_prefix_boundary()
    {
        var set = new ExclusionSet();
        var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

        Assert.True(set.IsDirectoryExcluded(ssh));
        Assert.True(set.IsDirectoryExcluded(ssh + Path.DirectorySeparatorChar)); // завершающий разделитель обрезается
        Assert.True(set.IsDirectoryExcluded(Path.Combine(ssh, "id_ed25519")));
        Assert.True(set.IsDirectoryExcluded(ssh.ToUpperInvariant())); // регистронезависимо

        Assert.False(set.IsDirectoryExcluded(ssh + "-backup")); // граница каталога
        Assert.False(set.IsDirectoryExcluded(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
    }

    [Fact]
    public void Extra_applications_and_directories_extend_defaults()
    {
        var set = new ExclusionSet(
            extraApplications: ["bank-client"],
            extraDirectories: ["C:\\BankData"]);

        Assert.True(set.IsApplicationExcluded("Bank-Client", null));     // регистронезависимо
        Assert.True(set.IsApplicationExcluded("bank-client.exe", null)); // расширение отбрасывается
        Assert.True(set.IsDirectoryExcluded("C:\\BankData\\vault"));
        Assert.False(set.IsDirectoryExcluded("C:\\BankDataExtra"));

        // Дефолты при этом не исчезают
        Assert.True(set.IsApplicationExcluded("keepass", null));
    }
}
