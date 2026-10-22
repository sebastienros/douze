using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow;

public class GlobTests
{
    private static bool Match(string pattern, string path) => GlobSet.Parse(pattern).IsMatch(path);

    [Fact]
    public void StarMatchesAtRootOnly()
    {
        Assert.True(Match("*.json", "/config.json"));
        Assert.False(Match("*.json", "/a/config.json"));
        Assert.False(Match("*.json", "/config.yml"));
    }

    [Fact]
    public void DoubleStarMatchesAnyDepth()
    {
        Assert.True(Match("**/*.json", "/config.json"));
        Assert.True(Match("**/*.json", "/a/config.json"));
        Assert.True(Match("**/*.json", "/a/b/c/config.json"));
        Assert.False(Match("**/*.json", "/a/config.jsonc"));
    }

    [Fact]
    public void LiteralEndsAreMatchedLikeThePattern()
    {
        // The literal prefix and suffix of a pattern reject paths before its regular expression runs.
        Assert.True(Match("**/index.md", "/index.md"));
        Assert.True(Match("**/index.md", "/a/b/index.md"));
        Assert.False(Match("**/index.md", "/a/xindex.md"));
        Assert.True(Match("docs/**/index.md", "/docs/index.md"));
        Assert.False(Match("docs/**/index.md", "/other/index.md"));
        Assert.True(Match("[ab].txt", "/a.txt"));
        Assert.True(Match("a{b,}/x", "/a/x"));
        Assert.True(Match("a\\*.md", "/a*.md"));
        Assert.False(Match("a\\*.md", "/ab.md"));

        // '**/*.md' and '**/*.{md,html}' match by their ends alone.
        Assert.True(Match("**/*.md", "/.md"));
        Assert.True(Match("**/*.md", "/a/.b/c.md"));
        Assert.False(Match("**/*.md", "/a/c.mdx"));
        Assert.True(Match("**/*.{md,html}", "/a/b.html"));
        Assert.True(Match("**/*-{a,}.txt", "/x/y-.txt"));
        Assert.False(Match("**/*.{md,html}", "/a/b.htm"));

        // 'prefix/**' matches the folder itself.
        Assert.True(Match("_content/**", "/_content"));
        Assert.False(Match("_content/**", "/_contents"));
    }

    [Fact]
    public void FileNameMatchesAtRootOnly()
    {
        Assert.True(Match("config.json", "/config.json"));
        Assert.True(Match("config.json", "config.json"));
        Assert.False(Match("config.json", "/a/config.json"));
        Assert.False(Match("config.json", "/aconfig.json"));
    }

    [Fact]
    public void LeadingSlashIsIgnored()
    {
        Assert.True(Match("/config.json", "/config.json"));
        Assert.False(Match("/config.json", "/a/config.json"));
        Assert.True(Match("/_content/**", "/_content/a/b.md"));
    }

    [Fact]
    public void QuestionMarkMatchesOneCharacterExceptSlash()
    {
        Assert.True(Match("file?.md", "/file1.md"));
        Assert.False(Match("file?.md", "/file10.md"));
        Assert.False(Match("file?.md", "/file.md"));
        Assert.False(Match("a?b", "/a/b"));
    }

    [Fact]
    public void BracesMatchAlternatives()
    {
        Assert.True(Match("**/*.{md,html}", "/a/index.md"));
        Assert.True(Match("**/*.{md,html}", "/index.html"));
        Assert.False(Match("**/*.{md,html}", "/index.liquid"));
        Assert.True(Match("{a,b{c,d}}.txt", "/bd.txt"));
        Assert.Throws<GlobFormatException>(() => Glob.Parse("*.{md,html"));
    }

    [Fact]
    public void NegatedCharacterClass()
    {
        Assert.True(Match("[!_]*", "/index.md"));
        Assert.False(Match("[!_]*", "/_data.yml"));
        Assert.True(Match("[a-c]*.md", "/b.md"));
        Assert.False(Match("[a-c]*.md", "/d.md"));

        var e = Assert.Throws<GlobFormatException>(() => Glob.Parse("content/["));
        Assert.Equal("unterminated character class", e.Message);
        Assert.Equal(8, e.Position);
    }

    [Fact]
    public void LastMatchingPatternWins()
    {
        var set = GlobSet.Parse("**/*.md", "!drafts/**", "drafts/keep.md");

        Assert.True(set.IsMatch("/index.md"));
        Assert.False(set.IsMatch("/drafts/wip.md"));
        Assert.True(set.IsMatch("/drafts/keep.md"));
        Assert.False(set.IsMatch("/style.css"));

        // A set that starts with a negation starts from "everything".
        var exclude = GlobSet.Parse("!**/*.yml");

        Assert.True(exclude.IsMatch("/index.md"));
        Assert.False(exclude.IsMatch("/a/data.yml"));
    }

    [Fact]
    public void DoubleStarInTheMiddle()
    {
        Assert.True(Match("content/**/x.md", "/content/x.md"));
        Assert.True(Match("content/**/x.md", "/content/a/b/x.md"));
        Assert.False(Match("content/**/x.md", "/other/a/x.md"));
        Assert.False(Match("content/**/x.md", "/content/a/y.md"));

        // A directory prefix matches everything below it.
        Assert.True(Match("content/**", "/content/a/b.yml"));
        Assert.False(Match("content/**", "/contents/a.md"));
    }

    [Fact]
    public void FixedPrefix()
    {
        Assert.Equal("/content/", GlobSet.Parse("content/**", "content/a/*.md", "!**/*.yml").FixedPrefix);
        Assert.Equal("/_data/", GlobSet.Parse("_data/**/*.{yml,json}").FixedPrefix);
        Assert.Equal("/content/a/", GlobSet.Parse("/content/a/*.md").FixedPrefix);
        Assert.Equal("/", GlobSet.Parse("*.json").FixedPrefix);
        Assert.Equal("/", GlobSet.Parse("content/**", "data/**").FixedPrefix);
        Assert.Null(GlobSet.Parse("!**/*.yml").FixedPrefix);
        Assert.Null(GlobSet.All.FixedPrefix);
    }
}
