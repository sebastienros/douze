using System.Text;
using Douze.Nodes;
using Douze.Workflow.Yaml;
using Xunit;

namespace Douze.Tests.Workflow.Yaml;

public class YamlStreamTests
{
    [Fact]
    public void NodesKeepTheirPosition()
    {
        // Columns count UTF-16 code units, not UTF-8 bytes; a byte order mark is not a character of the first line.
        var stream = YamlStream.Parse(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("a: été\nb:\n  - [x, 'é']\n")).ToArray());
        var root = Assert.IsType<YamlMapping>(Assert.Single(stream.Documents));
        var list = Assert.IsType<YamlSequence>(root[1].Value);
        var flow = Assert.IsType<YamlSequence>(list[0]);

        Assert.Equal((1, 1), stream.Position(root[0].Key));
        Assert.Equal((1, 4), stream.Position(root[0].Value));
        Assert.Equal((3, 3), stream.Position(list));
        Assert.Equal((3, 5), stream.Position(flow));
        Assert.Equal((3, 9), stream.Position(flow[1]));
    }

    [Fact]
    public void ScalarsAreTextWithTheirStyle()
    {
        var stream = YamlStream.Parse("- 1\n- '2'\n- \"a\\tb\"\n- |\n  x\n- \n");
        var items = Assert.IsType<YamlSequence>(Assert.Single(stream.Documents)).Cast<YamlScalar>().ToArray();

        Assert.Equal(["1", "2", "a\tb", "x\n", ""], items.Select(x => x.Value));
        Assert.Equal([true, false, false, false, true], items.Select(x => x.IsPlain));
    }

    [Fact]
    public void AliasesAreTheNodesOfTheirAnchors()
    {
        var stream = YamlStream.Parse("a: &x { b: 1 }\nc: *x\n");
        var root = Assert.IsType<YamlMapping>(Assert.Single(stream.Documents));

        Assert.Same(root[0].Value, root[1].Value);
    }

    [Fact]
    public void AnAliasWithoutAnchorIsAnError()
    {
        var error = Assert.Throws<YamlSyntaxException>(() => YamlStream.Parse("a: 1\nb: *x\n"));

        Assert.Equal((2, 4, "the alias '*x' has no anchor before it"), (error.Line, error.Column, error.Reason));
    }

    [Fact]
    public void SyntaxErrorsHaveThePositionOfTheText()
    {
        var error = Assert.Throws<YamlSyntaxException>(() => YamlStream.Parse("a: é\nb: 'x\n"));

        Assert.Equal((2, 4), (error.Line, error.Column));
        Assert.StartsWith("line 2, column 4: ", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("LineNumber", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDocumentIsRead()
    {
        Assert.Equal(2, YamlStream.Parse("a: 1\n---\nb: 2\n").Documents.Length);
        Assert.Empty(YamlStream.Parse("# a comment\n").Documents);
    }

    [Fact]
    public void FrontMatterTypesPlainScalars()
    {
        var values = Assert.IsType<NodeObject>(YamlHelper.Parse("i: 12\nd: 1.5\nb: True\ns: '12'\ndate: 2024-01-02\nlist: [1, x]\n"));

        Assert.Equal(12, Assert.IsType<NodeValue<int>>(values["i"]).Value);
        Assert.Equal(1.5m, Assert.IsType<NodeValue<decimal>>(values["d"]).Value);
        Assert.True(Assert.IsType<NodeValue<bool>>(values["b"]).Value);
        Assert.Equal("12", Assert.IsType<NodeValue<string>>(values["s"]).Value);
        Assert.Equal("2024-01-02", Assert.IsType<NodeValue<string>>(values["date"]).Value);
        Assert.Equal(2, Assert.IsType<NodeArray>(values["list"]).Count);
    }

    [Fact]
    public void FrontMatterErrorsHaveTheirPosition()
    {
        var error = Assert.Throws<YamlSyntaxException>(() => FrontMatter.Parse("---\ntitle: x\ntags: [a\n---\nbody"));
        Assert.StartsWith("line 2, column 9: ", error.Message, StringComparison.Ordinal);

        var key = Assert.Throws<YamlSyntaxException>(() => YamlHelper.Parse("[a, b]: c\n"));
        Assert.Equal("line 1, column 1: keys must be strings", key.Message);
    }
}
