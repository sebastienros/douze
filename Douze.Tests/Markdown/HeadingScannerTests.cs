using Douze.Markdown;
using Xunit;

namespace Douze.Tests.Markdown;

public class HeadingScannerTests
{
    [Fact]
    public void FindsHeadingsWithTheirDepthTextAndId()
    {
        var headings = HeadingScanner.Scan("<h1>Title</h1>\n<p>x</p>\n<h2 id=\"a&amp;b\" class=\"x\">Sub <code>Main</code> &amp; more</h2><H3>Upper</H3>");

        Assert.Equal(3, headings.Count);
        Assert.Equal((1, "Title", (string?)null), (headings[0].Depth, headings[0].Text, headings[0].Id));
        Assert.Equal((2, "Sub Main & more", "a&b"), (headings[1].Depth, headings[1].Text, headings[1].Id));
        Assert.Equal((3, "Upper", (string?)null), (headings[2].Depth, headings[2].Text, headings[2].Id));
    }

    [Fact]
    public void SkipsHeadingsInCodeScriptsAndComments()
    {
        var html = "<pre><code><h2>No</h2></code></pre><code><h3>No</h3></code><script>'<h2>No</h2>'</script><!-- <h2>No</h2> --><template><h2>No</h2></template><h2>Yes</h2>";

        Assert.Equal(["Yes"], HeadingScanner.Scan(html).Select(x => x.Text));
    }

    [Fact]
    public void IgnoresTagsThatAreNotHeadings()
    {
        Assert.Empty(HeadingScanner.Scan("<header>x</header><hr><h7>x</h7><h1"));
    }

    [Fact]
    public void CollapsesWhitespace()
    {
        // ASCII whitespace collapses; a no-break space is text (dropped by the slug, like GitHub).
        Assert.Equal("A\u00a0 B", Assert.Single(HeadingScanner.Scan("<h2>\n  A&nbsp;\n B </h2>")).Text);
    }

    [Fact]
    public void RewriteAddsIdsAndAppendsInOnePass()
    {
        const string html = "<h1>A</h1><p>x</p><h2 id=\"keep\">B</h2>";
        var headings = HeadingScanner.Scan(html);

        var result = HeadingScanner.Rewrite(html, headings, x => x.Text == "A" ? "a\"1" : "ignored", x => $"[{x.Depth}]");

        Assert.Equal("<h1 id=\"a&quot;1\">A[1]</h1><p>x</p><h2 id=\"keep\">B[2]</h2>", result);
    }

    [Fact]
    public void NoBreakSpacesStayInTheText()
    {
        // A no-break space is not a word separator: the slug drops it, as GitHub does.
        Assert.Equal("App\u00a0model", HeadingScanner.TextOf("App\u00a0model".AsSpan()));
        Assert.Equal("appmodel-x", Slugger.Slug("App\u00a0model x"));
    }
}
