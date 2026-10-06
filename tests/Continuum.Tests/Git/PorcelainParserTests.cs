using System.Linq;
using Continuum.Core.Domain;
using Continuum.Infrastructure.Git;
using Xunit;

namespace Continuum.Tests.Git;

/// <summary>
/// Разбор «git status --porcelain» (формат v1). Норматив: data-sources §4.2.
/// </summary>
public class PorcelainParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  \n")]
    public void Empty_input_gives_empty_list(string? input)
    {
        Assert.Empty(PorcelainParser.Parse(input));
    }

    [Fact]
    public void Untracked_marker_gives_untracked()
    {
        var files = PorcelainParser.Parse("?? new.txt\n");

        var file = Assert.Single(files);
        Assert.Equal("new.txt", file.Path);
        Assert.Equal(FileChangeKind.Untracked, file.Kind);
    }

    [Fact]
    public void Added_marker_gives_added()
    {
        var files = PorcelainParser.Parse("A  staged.txt\n");

        var file = Assert.Single(files);
        Assert.Equal("staged.txt", file.Path);
        Assert.Equal(FileChangeKind.Added, file.Kind);
    }

    [Fact]
    public void Deleted_marker_gives_deleted()
    {
        var files = PorcelainParser.Parse(" D gone.txt\n");

        var file = Assert.Single(files);
        Assert.Equal("gone.txt", file.Path);
        Assert.Equal(FileChangeKind.Deleted, file.Kind);
    }

    [Theory]
    [InlineData(" M file.txt")]  // изменён в рабочем дереве
    [InlineData("M  file.txt")]  // изменён в индексе
    [InlineData("MM file.txt")]  // и там, и там
    [InlineData(" T file.txt")]  // смена типа
    [InlineData("UU file.txt")]  // unmerged
    [InlineData("AM file.txt")]  // добавлен и изменён - важнее Added? нет: 'A' раньше по правилам
    [InlineData("!! file.txt")]  // экзотика не роняет парсер
    public void Other_markers_give_modified(string line)
    {
        var files = PorcelainParser.Parse(line + "\n");

        var file = Assert.Single(files);
        Assert.Equal("file.txt", file.Path);

        // 'A' в паре - Added, остальные - Modified
        var expected = line.Contains('A') ? FileChangeKind.Added : FileChangeKind.Modified;
        Assert.Equal(expected, file.Kind);
    }

    [Fact]
    public void Rename_gives_deleted_old_plus_added_new()
    {
        var files = PorcelainParser.Parse("R  old name.txt -> new name.txt\n");

        Assert.Equal(2, files.Count);
        Assert.Equal("old name.txt", files[0].Path);
        Assert.Equal(FileChangeKind.Deleted, files[0].Kind);
        Assert.Equal("new name.txt", files[1].Path);
        Assert.Equal(FileChangeKind.Added, files[1].Kind);
    }

    [Fact]
    public void Quoted_path_is_unquoted()
    {
        var files = PorcelainParser.Parse(" M \"path with spaces.txt\"\n");

        var file = Assert.Single(files);
        Assert.Equal("path with spaces.txt", file.Path);
    }

    [Fact]
    public void Escaped_quote_inside_quotes_is_unescaped()
    {
        var files = PorcelainParser.Parse(" M \"a\\\"b.txt\"\n");

        var file = Assert.Single(files);
        Assert.Equal("a\"b.txt", file.Path);
    }

    [Fact]
    public void Escaped_backslash_inside_quotes_is_unescaped()
    {
        var files = PorcelainParser.Parse(" M \"dir\\\\file.txt\"\n");

        var file = Assert.Single(files);
        Assert.Equal("dir\\file.txt", file.Path);
    }

    [Fact]
    public void Broken_lines_are_skipped()
    {
        var files = PorcelainParser.Parse("X\nXY\nXYnospace\n??\n M ok.txt\n");

        var file = Assert.Single(files);
        Assert.Equal("ok.txt", file.Path);
        Assert.Equal(FileChangeKind.Modified, file.Kind);
    }

    [Fact]
    public void Rename_without_arrow_is_skipped_as_broken()
    {
        var files = PorcelainParser.Parse("R  just-a-name.txt\n");

        Assert.Empty(files);
    }

    [Fact]
    public void Relative_paths_stay_relative()
    {
        // Склейка с корнем репозитория - на вызывающем
        var files = PorcelainParser.Parse(" M src/a.cs\n");

        var file = Assert.Single(files);
        Assert.Equal("src/a.cs", file.Path);
    }

    [Fact]
    public void Crlf_line_endings_are_handled()
    {
        var files = PorcelainParser.Parse("?? a.txt\r\n M b.txt\r\n");

        Assert.Equal(2, files.Count);
        Assert.Equal("a.txt", files[0].Path);
        Assert.Equal("b.txt", files[1].Path);
        Assert.All(files, f => Assert.DoesNotContain('\r', f.Path));
    }
}
