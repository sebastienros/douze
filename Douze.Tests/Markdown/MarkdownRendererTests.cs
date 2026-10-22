using System.Collections.Immutable;
using Douze.FileSystems;
using Douze.Markdown;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Markdown;

public class MarkdownRendererTests
{
    private readonly MemoryFileSystem _source = new();

    private MarkdownRenderer Renderer(MarkdownOptions? options = null) => new(options ?? new MarkdownOptions(), _source, new FPath("/_content/"));

    private Task<string> RenderAsync(string markdown, MarkdownOptions? options = null, List<MarkdownWarning>? warnings = null)
    {
        return Renderer(options).RenderAsync(markdown, new FPath("/_content/docs/"), null, warn: warnings == null ? null : warnings.Add).AsTask();
    }

    // Default extensions.

    [Theory]
    [InlineData("| a |\n|---|\n| 1 |", "<table>")]
    [InlineData("Text[^1].\n\n[^1]: The note.", "class=\"footnotes\"")]
    [InlineData("- [x] done\n- [ ] todo", "class=\"task-list-item\"")]
    [InlineData("Term\n:   Definition", "<dl>")]
    [InlineData("# Title {#custom .big}", "<h1 id=\"custom\" class=\"big\">Title</h1>")]
    [InlineData("See https://example.com.", "<a href=\"https://example.com\">https://example.com</a>")]
    [InlineData("> [!NOTE]\n> Text", "<aside")]
    [InlineData(":::tip\nText\n:::", "<aside")]
    public async Task DefaultExtensionsRenderTheirSyntax(string markdown, string expected)
    {
        Assert.Contains(expected, await RenderAsync(markdown));
    }

    [Fact]
    public async Task SmartyPantsTurnsApostrophesTypographic()
    {
        var options = new MarkdownOptions { Extensions = ["smartypants"] };

        Assert.Contains("<p>It\u2019s the 80\u2019s, &lsquo;quoted&rsquo; <code>it's</code> <b title='x'>y</b></p>", await RenderAsync("It's the 80's, 'quoted' `it's` <b title='x'>y</b>", options));
        Assert.Contains("<p>It's</p>", await RenderAsync("It's", new MarkdownOptions { Extensions = ["none"] }));
    }

    [Fact]
    public async Task NoneIsCommonMark()
    {
        var html = await RenderAsync("| a |\n|---|\n| 1 |\n\n# Title {#custom}", MarkdownOptions.None);

        Assert.DoesNotContain("<table>", html);
        Assert.Contains("<h1>Title {#custom}</h1>", html);
    }

    [Fact]
    public async Task AdvancedHasNoAutomaticHeadingIds()
    {
        var html = await RenderAsync("# Title\n\n| a |\n|---|\n| 1 |", new MarkdownOptions { Extensions = ["advanced"] });

        Assert.Contains("<h1>Title</h1>", html);
        Assert.Contains("<table>", html);
    }

    // Asides.

    [Fact]
    public async Task ContainerAndAlertRenderTheSameMarkup()
    {
        const string expected = "<aside aria-label=\"Note\" class=\"aside aside--note\"><p class=\"aside__title\" aria-hidden=\"true\">Note</p>\n<div class=\"aside__content\">\n<p>Some <em>text</em>.</p>\n</div>\n</aside>\n";

        Assert.Equal(expected, await RenderAsync(":::note\nSome *text*.\n:::"));
        Assert.Equal(expected, await RenderAsync("> [!NOTE]\n> Some *text*."));
    }

    [Theory]
    [InlineData("note", "Note")]
    [InlineData("tip", "Tip")]
    [InlineData("caution", "Caution")]
    [InlineData("danger", "Danger")]
    public async Task EveryKind(string kind, string title)
    {
        var expected = $"<aside aria-label=\"{title}\" class=\"aside aside--{kind}\"><p class=\"aside__title\" aria-hidden=\"true\">{title}</p>";

        Assert.StartsWith(expected, await RenderAsync($":::{kind}\nText\n:::"));
        Assert.StartsWith(expected, await RenderAsync($"> [!{kind.ToUpperInvariant()}]\n> Text"));
    }

    [Theory]
    [InlineData(":::tip[Before you start]", "Before you start")]
    [InlineData(":::tip Before you start", "Before you start")]
    [InlineData(":::tip[Use <b> & \"quotes\"]", "Use &lt;b&gt; &amp; &quot;quotes&quot;")]
    public async Task TitleFromTheInfoString(string opening, string title)
    {
        var html = await RenderAsync($"{opening}\nText\n:::");

        Assert.StartsWith($"<aside aria-label=\"{title}\" class=\"aside aside--tip\"><p class=\"aside__title\" aria-hidden=\"true\">{title}</p>", html);
    }

    [Fact]
    public async Task UnknownKindIsANoteWithAWarning()
    {
        var warnings = new List<MarkdownWarning>();

        var html = await RenderAsync("Intro\n\n:::details\nText\n:::", warnings: warnings);

        Assert.StartsWith("<p>Intro</p>\n<aside aria-label=\"Note\" class=\"aside aside--note\">", html);
        Assert.Equal(new MarkdownWarning(3, 1, "unknown aside kind 'details'; rendered as 'note'"), Assert.Single(warnings));
    }

    [Fact]
    public async Task GitHubAlertKindsMapToAsideKinds()
    {
        Assert.Contains("class=\"aside aside--note\"", await RenderAsync("> [!IMPORTANT]\n> Text"));
        Assert.Contains("class=\"aside aside--caution\"", await RenderAsync("> [!WARNING]\n> Text"));
    }

    [Fact]
    public async Task ClassIconsTitlesAndAttributes()
    {
        var options = new MarkdownOptions
        {
            Aside = AsideOptions.Default with
            {
                Class = "starlight-aside",
                Icons = ImmutableDictionary<string, string>.Empty.Add("tip", "<svg></svg>"),
                Titles = AsideOptions.Default.Titles.SetItem("tip", "Hint"),
            },
        };

        var html = await RenderAsync(":::tip{#first .wide}\nText\n:::", options);

        Assert.Equal("<aside id=\"first\" aria-label=\"Hint\" class=\"starlight-aside starlight-aside--tip wide\"><p class=\"starlight-aside__title\" aria-hidden=\"true\"><svg></svg>Hint</p>\n<div class=\"starlight-aside__content\">\n<p>Text</p>\n</div>\n</aside>\n", html);
    }

    [Fact]
    public async Task TitlesOfTheRender()
    {
        var html = await Renderer().RenderAsync(":::note\nText\n:::", null, null, new Dictionary<string, string> { ["note"] = "Remarque" });

        Assert.Contains("aria-label=\"Remarque\"", html);
    }

    [Fact]
    public async Task NoAsidesWithoutTheirExtensions()
    {
        var html = await RenderAsync(":::note\nText\n:::\n\n> [!NOTE]\n> Text", new MarkdownOptions { Extensions = ["pipetables"] });

        Assert.DoesNotContain("<aside", html);
    }

    // Code.

    [Fact]
    public async Task FenceWithLanguageAndMeta()
    {
        var html = await RenderAsync("```csharp title=\"Program.cs\" {3-4} <x>\nvar a = \"<b>\";\n```");

        Assert.Equal("<pre data-lang=\"csharp\" data-meta=\"title=&quot;Program.cs&quot; {3-4} &lt;x&gt;\"><code class=\"language-csharp\">var a = &quot;&lt;b&gt;&quot;;\n</code></pre>\n", html);
    }

    [Fact]
    public async Task FenceWithoutInfo()
    {
        Assert.Equal("<pre><code>a &lt; b\n</code></pre>\n", await RenderAsync("```\na < b\n```"));
    }

    [Fact]
    public async Task FenceWithLanguageOnly()
    {
        Assert.Equal("<pre data-lang=\"js\"><code class=\"language-js\">x\n</code></pre>\n", await RenderAsync("```js\nx\n```"));
    }

    [Fact]
    public async Task IndentedBlock()
    {
        Assert.Equal("<pre><code>a &lt; b\n</code></pre>\n", await RenderAsync("    a < b"));
    }

    [Fact]
    public async Task PassthroughLanguage()
    {
        Assert.Equal("<pre class=\"mermaid\">graph TD; A--&gt;B\n</pre>\n", await RenderAsync("```mermaid\ngraph TD; A-->B\n```"));
    }

    [Fact]
    public async Task ClassPrefix()
    {
        var html = await RenderAsync("```js\nx\n```", new MarkdownOptions { CodeClassPrefix = "lang-", PassthroughLanguages = [] });

        Assert.Equal("<pre data-lang=\"js\"><code class=\"lang-js\">x\n</code></pre>\n", html);
    }

    // Raw regions.

    [Fact]
    public async Task RawRegionIsKeptAsItIs()
    {
        var html = await RenderAsync("Before\n\n\u001E<div>\u001D\u001D*not emphasis*\u001D\u001D</div>\u001E\n\nAfter *this*");

        Assert.Equal("<p>Before</p>\n<div>\n\n*not emphasis*\n\n</div>\n<p>After <em>this</em></p>\n", html);
    }

    [Fact]
    public async Task RawRegionInterruptsAParagraph()
    {
        var html = await RenderAsync("Before\n\u001E<div></div>\u001E\nAfter");

        Assert.Equal("<p>Before</p>\n<div></div>\n<p>After</p>\n", html);
    }

    [Fact]
    public async Task RawRegionStaysInItsListItem()
    {
        var html = await RenderAsync("1. First\n\n   \u001E<div>\u001D\u001D<p>a</p>\u001D</div>\u001E\n2. Second");

        Assert.Equal("<ol>\n<li><p>First</p>\n<div>\n\n<p>a</p>\n</div>\n</li>\n<li><p>Second</p>\n</li>\n</ol>\n", html);
    }

    [Fact]
    public void EncodeMakesOneLine()
    {
        Assert.Equal("\u001E<div>\u001D\u001Da</div>\u001E", RawRegionExtension.Encode("<div>\r\n\na</div>\n"));
    }

    [Fact]
    public async Task StrayMarkersAreRemoved()
    {
        Assert.Equal("<p>Text and\nmore</p>\n", await RenderAsync("Text \u001Eand\u001Dmore\u001E"));
    }

    // Links.

    [Theory]
    [InlineData("other.md", "/docs/other/")]
    [InlineData("./other.md", "/docs/other/")]
    [InlineData("index.md", "/docs/")]
    [InlineData("../a/b.md", "/a/b/")]
    [InlineData("other.md#usage", "/docs/other/#usage")]
    [InlineData("guide/index.md?x=1", "/docs/guide/?x=1")]
    public async Task LinksToPagesAreRewritten(string link, string url)
    {
        await _source.WriteTextFileAsync("/_content/docs/other.md", "");
        await _source.WriteTextFileAsync("/_content/docs/index.md", "");
        await _source.WriteTextFileAsync("/_content/docs/guide/index.md", "");
        await _source.WriteTextFileAsync("/_content/a/b.md", "");

        Assert.Equal($"<p><a href=\"{url}\">x</a></p>\n", await RenderAsync($"[x]({link})"));
    }

    [Fact]
    public async Task ReferenceLinksAreRewritten()
    {
        await _source.WriteTextFileAsync("/_content/docs/other.md", "");

        Assert.Equal("<p><a href=\"/docs/other/\">x</a></p>\n", await RenderAsync("[x][o]\n\n[o]: other.md"));
    }

    [Theory]
    [InlineData("https://example.com/a.md")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("#section")]
    [InlineData("/docs/other.md")]
    [InlineData("//example.com/a.md")]
    [InlineData("other.html")]
    public async Task OtherLinksAreUntouched(string link)
    {
        await _source.WriteTextFileAsync("/_content/docs/other.md", "");

        Assert.Equal($"<p><a href=\"{link}\">x</a></p>\n", await RenderAsync($"[x]({link})"));
    }

    [Fact]
    public async Task ImagesAreUntouched()
    {
        await _source.WriteTextFileAsync("/_content/docs/other.md", "");

        Assert.Equal("<p><img src=\"other.md\" alt=\"x\" /></p>\n", await RenderAsync("![x](other.md)"));
    }

    [Fact]
    public async Task MissingTargetIsLeftWithAnAbsentDependency()
    {
        var deps = new DependencyTracker();

        var html = await Renderer().RenderAsync("[x](missing.md)", new FPath("/_content/docs/"), deps);

        Assert.Equal("<p><a href=\"missing.md\">x</a></p>\n", html);
        Assert.Equal([(new FPath("/_content/docs/missing.md"), DependencySet.Absent)], deps.ToSet().Templates);
    }

    [Fact]
    public async Task LinksAreLeftWhenDisabled()
    {
        await _source.WriteTextFileAsync("/_content/docs/other.md", "");

        Assert.Equal("<p><a href=\"other.md\">x</a></p>\n", await RenderAsync("[x](other.md)", new MarkdownOptions { RewriteLinks = false }));
    }

    // Pipeline.

    [Theory]
    [InlineData("tabels", "unknown Markdown extension 'tabels'; did you mean 'pipetables'?")]
    [InlineData("footnote", "unknown Markdown extension 'footnote'; did you mean 'footnotes'?")]
    [InlineData("xyz", "unknown Markdown extension 'xyz'")]
    [InlineData("autoidentifiers", "use the headings step for heading ids")]
    public void InvalidExtensionNames(string name, string message)
    {
        Assert.Equal(message, MarkdownPipelineFactory.Validate(name));
    }

    [Fact]
    public void OptionsHash()
    {
        var options = new MarkdownOptions();
        var hash = options.Hash;

        Assert.Equal(hash, new MarkdownOptions().Hash);
        Assert.NotEqual(hash, (options with { RewriteLinks = false }).Hash);
        Assert.NotEqual(hash, (options with { Aside = options.Aside with { Class = "callout" } }).Hash);
        Assert.NotEqual(hash, (options with { Extensions = ["pipetables"] }).Hash);
        Assert.NotEqual(hash, (options with { Images = options.Images with { Quality = 80 } }).Hash);
    }

    [Fact]
    public async Task ParallelRendersOnThePoolGiveTheSameOutput()
    {
        var renderer = Renderer();
        var markdown = string.Join("\n\n", Enumerable.Range(0, 50).Select(i => $"## Section {i}\n\n:::tip\nText *{i}*\n:::\n\n```js\nvar a = {i};\n```"));
        var expected = await renderer.RenderAsync(markdown, null, null);

        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => renderer.RenderAsync(markdown, null, null).AsTask(), TestContext.Current.CancellationToken)));

        Assert.All(results, x => Assert.Equal(expected, x));
    }
}
