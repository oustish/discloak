namespace Bdxl.Tests;

public class GitIgnoreTests
{
    static GitIgnore Rules(params string[] lines) => new(lines);

    [Theory]
    [InlineData("a.tmp", true)]
    [InlineData("deep/down/a.tmp", true)]
    [InlineData("a.tmpx", false)]
    [InlineData("tmp", false)]
    public void Unanchored_pattern_matches_at_any_depth(string path, bool ignored) =>
        Assert.Equal(ignored, Rules("*.tmp").IsIgnored(path));

    [Theory]
    [InlineData("node_modules/x.js", true)]
    [InlineData("web/node_modules/lib/x.js", true)]
    [InlineData("node_modules", false)]          // a FILE called node_modules: the rule is for directories only
    public void Trailing_slash_matches_directories_only(string path, bool ignored) =>
        Assert.Equal(ignored, Rules("node_modules/").IsIgnored(path));

    [Theory]
    [InlineData("cache/a/b.bin", true)]
    [InlineData("x/cache/a.bin", false)]
    public void Leading_slash_anchors_to_the_source_folder(string path, bool ignored) =>
        Assert.Equal(ignored, Rules("/cache/**").IsIgnored(path));

    [Theory]
    [InlineData("doc/a.txt", true)]
    [InlineData("doc/sub/a.txt", false)]
    [InlineData("x/doc/a.txt", false)]
    public void Slash_in_the_middle_anchors_and_star_stays_in_one_folder(string path, bool ignored) =>
        Assert.Equal(ignored, Rules("doc/*.txt").IsIgnored(path));

    [Theory]
    [InlineData("temp", true)]
    [InlineData("a/b/temp", true)]
    [InlineData("a/temp/file", true)]
    [InlineData("a/temporary", false)]
    public void Double_star_slash_matches_any_number_of_folders(string path, bool ignored) =>
        Assert.Equal(ignored, Rules("**/temp").IsIgnored(path));

    [Theory]
    [InlineData("file1.txt", true)]
    [InlineData("file10.txt", false)]
    public void Question_mark_is_one_character(string path, bool ignored) =>
        Assert.Equal(ignored, Rules("file?.txt").IsIgnored(path));

    [Fact]
    public void Character_classes_and_negated_classes()
    {
        var g = Rules("[ab].txt", "[!xy].log");
        Assert.True(g.IsIgnored("a.txt"));
        Assert.False(g.IsIgnored("c.txt"));
        Assert.True(g.IsIgnored("z.log"));
        Assert.False(g.IsIgnored("x.log"));
    }

    [Fact]
    public void Later_negation_re_includes_a_file()
    {
        var g = Rules("*.log", "!keep.log");
        Assert.True(g.IsIgnored("a.log"));
        Assert.False(g.IsIgnored("keep.log"));
        Assert.False(g.Explain("keep.log", out string? rule));
        Assert.Equal("!keep.log", rule);
    }

    [Fact]
    public void A_file_inside_an_ignored_folder_cannot_be_re_included()
    {
        // same as git: once a directory is excluded, nothing below it comes back
        var g = Rules("build/", "!build/keep.txt");
        Assert.True(g.Explain("build/keep.txt", out string? rule));
        Assert.Equal("build/", rule);
    }

    [Fact]
    public void Comments_blank_lines_and_escapes()
    {
        var g = Rules("# a comment", "", "   ", "\\#hash", "\\!bang");
        Assert.Equal(2, g.RuleCount);
        Assert.True(g.IsIgnored("#hash"));
        Assert.True(g.IsIgnored("!bang"));
    }

    [Fact]
    public void No_rules_ignores_nothing()
    {
        Assert.False(Rules().Explain("anything/at/all", out string? rule));
        Assert.Null(rule);
    }
}
