using Douze.Highlighting;
using Xunit;

namespace Douze.Tests.Highlighting;

public class CodeMetaTests
{
    [Fact]
    public void KeysValuesAndDelimiters()
    {
        var meta = new CodeMeta("""title="Program.cs" {1-3} ins={4,6} "text" /re(g)ex/ frame=terminal showLineNumbers wrap=false del='x y' startLineNumber=5""");

        Assert.Equal("Program.cs", meta.GetString("title"));
        Assert.Equal("terminal", meta.GetString("frame"));
        Assert.True(meta.GetBoolean("showLineNumbers"));
        Assert.False(meta.GetBoolean("wrap"));
        Assert.Equal(5, meta.GetInteger("startLineNumber"));
        Assert.Equal(["1-3"], meta.List([""], MetaKind.Range).Select(x => x.Value));
        Assert.Equal(["4,6"], meta.GetRanges("ins"));
        Assert.Equal(["text"], meta.List([""], MetaKind.String).Select(x => x.Value));
        Assert.Equal(["re(g)ex"], meta.List([""], MetaKind.Regex).Select(x => x.Value));
        Assert.Equal("x y", meta.GetString("del"));
    }

    [Fact]
    public void KeysCompareCaseInsensitivelyAndTheLastWins()
    {
        var meta = new CodeMeta("title=a TITLE=\"b\"");

        Assert.Equal("b", meta.GetString("Title"));
    }

    [Fact]
    public void EscapedDelimiters()
    {
        var meta = new CodeMeta("""title="a \"quoted\" title" "x\\y" """);

        Assert.Equal("a \"quoted\" title", meta.GetString("title"));
        Assert.Equal(["x\\y"], meta.List([""], MetaKind.String).Select(x => x.Value));
    }

    [Theory]
    [InlineData("1,3-5", new[] { 1, 3, 4, 5 })]
    [InlineData("2..4", new[] { 2, 3, 4 })]
    [InlineData("2...4", new[] { 2, 3 })]
    [InlineData("5-3", new[] { 5, 4, 3 })]
    [InlineData("x,7", new[] { 7 })]
    public void Ranges(string range, int[] lines)
    {
        Assert.Equal(lines, CodeBlockBuilder.ParseRange(range));
    }
}
