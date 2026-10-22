using Douze.FileSystems;
using Douze.Nodes;
using Douze.Tests.Workflow.Steps;
using Xunit;

namespace Douze.Tests.Liquid.Tags;

public class MarkdownTagsTests : StepTestBase
{
    private async Task<string> RenderAsync(string content, string path = "/_content/page.md", string output = "/page/index.html")
    {
        await Source.WriteTextFileAsync(path, content);
        await BuildSuccessfullyAsync();
        return await Output.ReadAllTextAsync(output);
    }

    // include_markdown.

    [Fact]
    public async Task IncludeIsRenderedAsPartOfThePage()
    {
        await Source.WriteTextFileAsync("/_content/partials/options.md", "---\npermalink: false\nversion: 1.0\n---\n## Options of {{ title }} {{ version }}\n\n- `--all`\n");

        var html = await RenderAsync("---\ntitle: Tool\n---\n# Tool\n\n{% include_markdown \"partials/options.md\" %}\n\nAfter\n");

        Assert.Equal("<h1>Tool</h1>\n<h2>Options of Tool 1.0</h2>\n<ul>\n<li><code>--all</code></li>\n</ul>\n<p>After</p>\n", html);
    }

    [Fact]
    public async Task PageNamesWinOverTheIncludeFrontMatterAndArgumentsWinOverBoth()
    {
        await Source.WriteTextFileAsync("/_content/partials/x.md", "---\npermalink: false\nversion: 1.0\nlabel: from include\nextra: from include\n---\n{{ version }}, {{ label }}, {{ extra }}");

        var html = await RenderAsync("---\nversion: 2.0\n---\n{% include_markdown \"partials/x.md\", extra: \"from argument\" %}\n");

        Assert.Equal("<p>2.0, from include, from argument</p>\n", html);
    }

    [Fact]
    public async Task RelativePathsStartFromTheIncludingFile()
    {
        await Source.WriteTextFileAsync("/_content/docs/shared/a.md", "---\npermalink: false\n---\nA, then {% include_markdown \"../b.md\" %}");
        await Source.WriteTextFileAsync("/_content/docs/b.md", "---\npermalink: false\n---\nB");

        var html = await RenderAsync("{% include_markdown \"./shared/a.md\" %}", "/_content/docs/page.md", "/docs/page/index.html");

        Assert.Equal("<p>A, then B</p>\n", html);
    }

    [Fact]
    public async Task FencesOfAnIncludeAreNotLiquid()
    {
        await Source.WriteTextFileAsync("/_content/partials/code.md", "---\npermalink: false\n---\n```yaml\nkey: ${{ value }}\n```\n");

        var html = await RenderAsync("{% include_markdown \"partials/code.md\" %}");

        Assert.Equal("<pre data-lang=\"yaml\"><code class=\"language-yaml\">key: ${{ value }}\n</code></pre>\n", html);
    }

    [Fact]
    public async Task IncludeInsideAListItemStaysInTheItem()
    {
        await Source.WriteTextFileAsync("/_content/partials/steps.md", "---\npermalink: false\n---\nFirst paragraph.\n\nSecond paragraph.\n\n```bash\nrun it\n\nagain\n```\n");

        var html = await RenderAsync("1. Install\n\n   {% include_markdown \"partials/steps.md\" %}\n2. Run\n");

        Assert.Equal("""
            <ol>
            <li><p>Install</p>
            <p>First paragraph.</p>
            <p>Second paragraph.</p>
            <pre data-lang="bash"><code class="language-bash">run it

            again
            </code></pre>
            </li>
            <li><p>Run</p>
            </li>
            </ol>

            """, html);
    }

    [Fact]
    public async Task IncludeInAnHtmlPageIsRenderedToHtml()
    {
        await Source.WriteTextFileAsync("/_content/partials/x.md", "---\npermalink: false\n---\n*{{ name }}*");

        var html = await RenderAsync("<main>{% include_markdown \"partials/x.md\", name: \"Douze\" %}</main>", "/_content/page.html");

        Assert.Equal("<main><p><em>Douze</em></p>\n</main>", html);
    }

    [Fact]
    public async Task MissingIncludeIsAnError()
    {
        await Source.WriteTextFileAsync("/_content/page.md", "{% include_markdown \"nope.md\" %}");

        var result = await BuildAsync();

        Assert.Equal("error: render: /page/index.md: include_markdown: 'nope.md' not found in '/_content/'", Assert.Single(result.Errors).ToString());
    }

    [Fact]
    public async Task RecursionIsLimited()
    {
        await Source.WriteTextFileAsync("/_content/partials/loop.md", "---\npermalink: false\n---\n{% include_markdown \"partials/loop.md\" %}");
        await Source.WriteTextFileAsync("/_content/page.md", "{% include_markdown \"partials/loop.md\" %}");

        var result = await BuildAsync();

        var error = Assert.Single(result.Errors, x => x.File?.FullName == "/page/index.md");
        Assert.StartsWith("include_markdown: more than 16 nested includes: /_content/partials/loop.md -> /_content/partials/loop.md", error.Message);
    }

    [Fact]
    public async Task IncludeRecordsADependency()
    {
        await Source.WriteTextFileAsync("/_content/partials/x.md", "---\npermalink: false\n---\nx");
        await Source.WriteTextFileAsync("/_content/page.md", "{% include_markdown \"partials/x.md\" %}");

        var result = await BuildSuccessfullyAsync();

        var unit = result["render"].Results.Single(x => x.Key.Unit == "/page/index.md");
        Assert.Contains(unit.Dependencies.Templates, x => x.Path.FullName == "/_content/partials/x.md");
    }

    [Fact]
    public async Task IncludeHeadingsAreInThePageToc()
    {
        await Source.WriteTextFileAsync("/partials/x.md", "## From the include\n");
        await Source.WriteTextFileAsync("/page.md", "## Own\n\n{% include_markdown \"partials/x.md\" %}\n");

        var result = await BuildSuccessfullyAsync(Workflow(
            Step("pages", "frontmatter", inputs: ["page.md"]),
            Step("render", "liquid", needs: ["pages"]),
            Step("markdown", "markdown", needs: ["render"]),
            Step("headings", "headings", needs: ["markdown"])));

        var toc = (NodeArray)Outputs(result["headings"]).Single().Metadata.Own["toc"]!;
        Assert.Equal(["own", "from-the-include"], toc.Cast<NodeObject>().Select(x => x["id"]!.GetValue<string>()));
    }

    // markdown.

    [Fact]
    public async Task MarkdownBlockInALayout()
    {
        await Source.WriteTextFileAsync("/_includes/layout.html", "<header>{% markdown %}**{{ title }}** and [home](/){% endmarkdown %}</header>{{ content }}");

        var html = await RenderAsync("---\ntitle: Douze\nlayout: layout.html\n---\nText\n");

        Assert.Equal("<header><p><strong>Douze</strong> and <a href=\"/\">home</a></p>\n</header><p>Text</p>\n", html);
    }

    [Fact]
    public async Task MarkdownBlockInAMarkdownPageIsARawRegion()
    {
        var html = await RenderAsync("- Item\n\n  {% markdown %}\n# Heading\n\nText\n{% endmarkdown %}\n- Next\n");

        Assert.Equal("<ul>\n<li><p>Item</p>\n<h1>Heading</h1>\n<p>Text</p>\n</li>\n<li><p>Next</p>\n</li>\n</ul>\n", html);
    }
}
