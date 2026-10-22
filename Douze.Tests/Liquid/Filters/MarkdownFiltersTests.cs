using Douze.FileSystems;
using Douze.Liquid.Filters;
using Xunit;

namespace Douze.Tests.Liquid.Filters;

public class MarkdownFiltersTests : SiteTestBase
{
    /// <summary>
    /// Renders <paramref name="template"/> as an HTML page whose <c>text</c> is <paramref name="text"/>.
    /// </summary>
    private async Task<string> RenderAsync(string template, string text)
    {
        await Source.WriteTextFileAsync("/_data/text.json", "\"" + text.Replace("\n", "\\n") + "\"");
        await Source.WriteTextFileAsync("/_content/page.html", template);
        await BuildSuccessfullyAsync();
        return await Output.ReadAllTextAsync("/page/index.html");
    }

    [Fact]
    public async Task Markdownify()
    {
        Assert.Equal("<p>Some <em>text</em></p>\n", await RenderAsync("{{ text | markdownify }}", "Some *text*"));
    }

    [Fact]
    public async Task MarkdownifyInline()
    {
        Assert.Equal("<span>Some <em>text</em></span>", await RenderAsync("<span>{{ text | markdownify: inline: true }}</span>", "Some *text*"));
    }

    [Fact]
    public async Task MarkdownifyInlineKeepsSeveralParagraphs()
    {
        Assert.Equal("<p>a</p>\n<p>b</p>\n", await RenderAsync("{{ text | markdownify: inline: true }}", "a\n\nb"));
    }

    [Fact]
    public async Task StripMarkdown()
    {
        Assert.Equal("A title &amp; text", await RenderAsync("{{ text | strip_markdown }}", "# A *title* &amp; [text](/x)"));
    }

    [Fact]
    public async Task ExcerptIsTheFirstParagraph()
    {
        Assert.Equal("<p>First <strong>one</strong>.</p>", await RenderAsync("{{ text | excerpt }}", "# Title\n\nFirst **one**.\n\nSecond."));
    }

    [Fact]
    public async Task ExcerptStopsAtTheMoreMarker()
    {
        Assert.Equal("<p>One.</p>\n<p>Two.</p>\n", await RenderAsync("{{ text | excerpt }}", "One.\n\nTwo.\n\n<!-- more -->\n\nThree."));
    }

    [Fact]
    public void FirstParagraphSkipsPre()
    {
        Assert.Equal("<p class=\"x\">a</p>", MarkdownFilters.FirstParagraph("<pre>code</pre><p class=\"x\">a</p><p>b</p>"));
        Assert.Equal("", MarkdownFilters.FirstParagraph("<pre>code</pre>"));
    }
}
