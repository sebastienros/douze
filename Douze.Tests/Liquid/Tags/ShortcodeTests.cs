using Douze.FileSystems;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Liquid.Tags;

public class ShortcodeTests : SiteTestBase
{
    private const string Shortcodes = "/_includes/shortcodes/";

    private async Task WriteShortcodeAsync(string name, string content)
    {
        await Source.WriteTextFileAsync(Shortcodes + name, content);
    }

    /// <summary>
    /// Builds a site with the page <c>/_content/page.md</c> (or <paramref name="path"/>) and returns its output.
    /// </summary>
    private async Task<string> RenderAsync(string content, string path = "/_content/page.md", string output = "/page/index.html")
    {
        await Source.WriteTextFileAsync(path, content);
        await BuildSuccessfullyAsync();
        return await Output.ReadAllTextAsync(output);
    }

    private async Task WriteTabsAsync()
    {
        await WriteShortcodeAsync("tabs.liquid", "---\nblock: true\n---\n<div class=\"tabs\">\n\n{{ body }}\n\n</div>");
        await WriteShortcodeAsync("tab.liquid", "---\nblock: true\nargs: [label]\n---\n<section data-label=\"{{ label }}\">{{ body }}</section>");
    }

    [Fact]
    public async Task BlockShortcodeRendersItsBodyAsMarkdown()
    {
        await WriteTabsAsync();

        var html = await RenderAsync("{% tabs %}\nSome **bold** text\n{% endtabs %}\n\nAfter *this*\n");

        // The blank lines of the output do not end an HTML block: the whole output is kept (raw region).
        Assert.Equal("<div class=\"tabs\">\n\n<p>Some <strong>bold</strong> text</p>\n\n\n</div>\n<p>After <em>this</em></p>\n", html);
    }

    [Fact]
    public async Task FenceInsideATabBodyIsRenderedAsAFence()
    {
        await WriteTabsAsync();

        var html = await RenderAsync("{% tabs %}\n{% tab \"C#\" %}\nCode:\n\n```csharp title=\"Program.cs\"\nvar x = \"{{ not liquid }}\";\n```\n{% endtab %}\n{% endtabs %}\n");

        Assert.Contains("<section data-label=\"C#\"><p>Code:</p>\n<pre data-lang=\"csharp\" data-meta=\"title=&quot;Program.cs&quot;\"><code class=\"language-csharp\">var x = &quot;{{ not liquid }}&quot;;\n</code></pre>\n</section>", html);
    }

    [Fact]
    public async Task NestedBlocksPutTheInnerHtmlInsideTheOuter()
    {
        await WriteTabsAsync();
        await WriteShortcodeAsync("pivot.liquid", "---\nblock: true\n---\n<div class=\"pivot\">{{ body }}</div>");

        var html = await RenderAsync("{% pivot %}\n{% tabs %}\n{% tab \"A\" %}\n*a*\n{% endtab %}\n{% tab \"B\" %}\n*b*\n{% endtab %}\n{% endtabs %}\n{% endpivot %}\n");

        Assert.Equal("<div class=\"pivot\"><div class=\"tabs\">\n\n<section data-label=\"A\"><p><em>a</em></p>\n</section>\n<section data-label=\"B\"><p><em>b</em></p>\n</section>\n\n\n</div>\n</div>\n", html);
    }

    [Fact]
    public async Task TabSetInsideAListItemStaysInTheItem()
    {
        await WriteTabsAsync();

        var html = await RenderAsync("""
            1. Install the package

               {% tabs %}
               {% tab "npm" %}
               ```bash
               npm install x
               ```
               {% endtab %}
               {% tab "yarn" %}
               With yarn.
               {% endtab %}
               {% endtabs %}
            2. Run it

            """);

        Assert.Equal("""
            <ol>
            <li><p>Install the package</p>
            <div class="tabs">

            <section data-label="npm"><pre data-lang="bash"><code class="language-bash">npm install x
            </code></pre>
            </section>
            <section data-label="yarn"><p>With yarn.</p>
            </section>


            </div>
            </li>
            <li><p>Run it</p>
            </li>
            </ol>

            """, html);
    }

    [Fact]
    public async Task InlinePairedShortcodeHasNoParagraph()
    {
        await WriteShortcodeAsync("kbd.liquid", "---\nblock: true\ndisplay: inline\n---\n<kbd>{{ body }}</kbd>");

        var html = await RenderAsync("Press {% kbd %}**Ctrl**{% endkbd %} now.\n");

        Assert.Equal("<p>Press <kbd><strong>Ctrl</strong></kbd> now.</p>\n", html);
    }

    [Fact]
    public async Task InlineShortcodeStaysInItsParagraph()
    {
        await WriteShortcodeAsync("badge.liquid", "---\nargs: [label]\n---\n<span class=\"badge badge--{{ variant | default: 'note' }}\">{{ label }}</span>");

        var html = await RenderAsync("Install {% badge \"v2\", variant: \"tip\" %} or {% badge \"v1\" %} now.\n");

        Assert.Equal("<p>Install <span class=\"badge badge--tip\">v2</span> or <span class=\"badge badge--note\">v1</span> now.</p>\n", html);
    }

    [Fact]
    public async Task ShortcodeTemplateUsesAnotherShortcode()
    {
        await WriteShortcodeAsync("badge.liquid", "---\nargs: [label]\n---\n<span class=\"badge\">{{ label }}</span>");
        await WriteShortcodeAsync("card.liquid", "---\nblock: true\n---\n<div class=\"card\">{% badge title %}{{ body }}</div>");

        var html = await RenderAsync("{% card title: \"New\" %}\nText\n{% endcard %}\n");

        Assert.Equal("<div class=\"card\"><span class=\"badge\">New</span><p>Text</p>\n</div>\n", html);
    }

    [Fact]
    public async Task ArgumentsAreValuesOrExpressions()
    {
        await Source.WriteTextFileAsync("/_data/product.yml", "name: Douze");
        await WriteShortcodeAsync("show.liquid", "---\nargs: [first]\n---\n{{ first }}|{{ args[1] }}|{{ args.size }}|{{ named }}|{{ title }}|{{ product.name }}");

        var html = await RenderAsync("---\ntitle: Page\n---\n{% show title, 2, named: product.name %}\n");

        // Positional values, named ones, and the page's names and data.
        Assert.Equal("<p>Page|2|2|Douze|Page|Douze</p>\n", html);
    }

    [Fact]
    public async Task DisplayBlockOnASingleTag()
    {
        await WriteShortcodeAsync("figure.liquid", "---\ndisplay: block\n---\n<figure>\n\n<img src=\"{{ src }}\">\n\n</figure>");

        var html = await RenderAsync("Before\n\n{% figure src: \"a.png\" %}\n\n*After*\n");

        Assert.Equal("<p>Before</p>\n<figure>\n\n<img src=\"a.png\">\n\n</figure>\n<p><em>After</em></p>\n", html);
    }

    [Fact]
    public async Task HtmlPageGetsTheBodyAsText()
    {
        await WriteTabsAsync();

        var html = await RenderAsync("<main>{% tabs %}**not Markdown**{% endtabs %}</main>", "/_content/page.html", "/page/index.html");

        Assert.Equal("<main><div class=\"tabs\">\n\n**not Markdown**\n\n</div></main>", html);
    }

    [Fact]
    public async Task UnknownTagSuggestsAShortcode()
    {
        await WriteTabsAsync();
        await Source.WriteTextFileAsync("/_content/page.md", "Text\n{% tabss %}\n");

        var result = await BuildAsync();

        var error = Assert.Single(result.Errors);
        Assert.Equal("/page/index.md", error.File?.FullName);
        Assert.Equal(2, error.Location?.Line);
        Assert.EndsWith("Unknown tag 'tabss'; did you mean 'tabs'?", error.Message);
    }

    [Fact]
    public async Task ParseErrorIsReportedAtScanWithFileAndPosition()
    {
        await WriteShortcodeAsync("broken.liquid", "---\nblock: true\n---\nline\nline {% if %}");
        await Source.WriteTextFileAsync("/_content/page.md", "Text");

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(() => BuildAsync());

        var error = Assert.Single(exception.Errors);
        Assert.Equal(Shortcodes + "broken.liquid", error.Location?.File);
        Assert.Equal(5, error.Location?.Line);
    }

    [Fact]
    public async Task InvalidNamesAndFrontMatterAreReported()
    {
        await WriteShortcodeAsync("include.liquid", "x");
        await WriteShortcodeAsync("link-card.liquid", "x");
        await WriteShortcodeAsync("endslot.liquid", "x");
        await WriteShortcodeAsync("bad.liquid", "---\nblock: yes please\ndisplya: block\n---\nx");

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(() => BuildAsync());

        Assert.Equal([
            "/_includes/shortcodes/bad.liquid(1,1): error: 'block' of shortcode 'bad' must be true or false",
            "/_includes/shortcodes/bad.liquid(1,1): error: unknown key 'displya' in the front matter of shortcode 'bad'; expected 'block', 'display' or 'args'",
            "/_includes/shortcodes/endslot.liquid(1,1): error: shortcode 'endslot' hides a built-in tag; rename the file",
            "/_includes/shortcodes/include.liquid(1,1): error: shortcode 'include' hides a built-in tag; rename the file",
            "/_includes/shortcodes/link-card.liquid(1,1): error: 'link-card.liquid' is not a shortcode name: use lowercase letters, digits and underscores",
        ], exception.Errors.Select(x => x.ToString()));
    }

    [Fact]
    public async Task RenderErrorNamesTheShortcodeAndThePage()
    {
        await WriteShortcodeAsync("boom.liquid", "---\nblock: true\n---\n<div>\n  {% error \"bad \" | append: kind %}</div>");
        await Source.WriteTextFileAsync("/_content/page.md", "{% boom kind: \"value\" %}x{% endboom %}");

        var result = await BuildAsync();

        var error = Assert.Single(result.Errors);
        Assert.Equal("error: render: /page/index.md: shortcode 'boom' (/_includes/shortcodes/boom.liquid(5,3)): bad value", error.ToString());
    }

    [Fact]
    public async Task RenderRecordsTheShortcodeFile()
    {
        await WriteShortcodeAsync("badge.liquid", "<b>{{ args[0] }}</b>");
        await Source.WriteTextFileAsync("/_content/page.md", "{% badge \"x\" %}");

        var result = await BuildSuccessfullyAsync();

        var unit = result["render"].Results.Single();
        Assert.Contains(unit.Dependencies.Templates, x => x.Path.FullName == Shortcodes + "badge.liquid");
    }

    [Fact]
    public async Task SlotsPlaceTheirBodyInADiv()
    {
        await WriteShortcodeAsync("card.liquid", "---\nblock: true\n---\n<div class=\"card\">{{ body }}</div>");

        var html = await RenderAsync("{% card %}\n{% slot \"title\" %}\n*Title*\n{% endslot %}\nText\n{% endcard %}\n");

        Assert.Equal("<div class=\"card\"><div data-slot=\"title\"><p><em>Title</em></p>\n</div>\n<p>Text</p>\n</div>\n", html);
    }

    [Fact]
    public async Task ShortcodesFolderDefaultsToTheIncludesFolder()
    {
        await Source.WriteTextFileAsync("/_includes/layout.html", "x");

        var site = await SiteOptions.DetectAsync(Source, Output, null, null);

        Assert.Equal(new FPath("/_includes/shortcodes/"), site.ShortcodesFolder);
    }
}
