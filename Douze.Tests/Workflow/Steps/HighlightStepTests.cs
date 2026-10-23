using System.Net;
using Douze.FileSystems;
using Douze.Nodes;
using Douze.Progress;
using Douze.Tests.Workflow.Cache;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow.Steps;

public sealed class HighlightStepTests : CacheTestBase
{
    private static WorkflowDefinition Highlight(NodeObject? with = null) => StepTestBase.Workflow(
        StepTestBase.Step("pages", "frontmatter", inputs: ["docs/**"]),
        StepTestBase.Step("markdown", "markdown", needs: ["pages"]),
        StepTestBase.Step("highlight", "highlight", needs: ["markdown"], with: with));

    private async Task<string> RenderAsync(string markdown, NodeObject? with = null, string path = "/docs/a.md")
    {
        await Source.WriteTextFileAsync(path, markdown);
        var result = await BuildCachedAsync(Highlight(with));
        var html = Path.ChangeExtension(path, ".html");
        return await StepTestBase.TextAsync(StepTestBase.Outputs(result["highlight"]).Single(x => x.Path.ToString() == html));
    }

    private static string Fence(string language, string code, string meta = "") => $"```{language} {meta}\n{code}\n```\n";

    [Fact]
    public async Task HighlightsWithAVariablePerTheme()
    {
        var html = await RenderAsync(Fence("csharp", "var x = 1; // one"), StepTestBase.With(("min-contrast", NodeValue.Create(0))));

        Assert.Contains("<figure class=\"code code-code\" data-language=\"csharp\">", html);
        Assert.Contains("<span style=\"--0:#F97583;--1:#D73A49\">var</span>", html);
        Assert.Contains("<span style=\"--0:#6A737D;--1:#6A737D\">// one</span>", html);
        Assert.Contains("data-code=\"var x = 1; // one\"", html);
        Assert.DoesNotContain("<pre data-lang", html);
    }

    [Fact]
    public async Task TitlesAndFrames()
    {
        var html = await RenderAsync(
            Fence("ts", "const a = 1;", "title=\"app.ts\"")
            + Fence("ts", "// src/main.ts\n\nconst b = 2;")
            + Fence("bash", "# install\nnpm install")
            + Fence("bash", "#!/bin/bash\necho hi")
            + Fence("bash", "echo hi", "frame=none"));

        Assert.Contains("<figure class=\"code code-code has-title\" data-language=\"ts\"><figcaption class=\"code-title\">app.ts</figcaption>", html);

        // The file name of a comment is the title, and the comment and the blank line after it are removed.
        Assert.Contains("<figcaption class=\"code-title\">src/main.ts</figcaption><pre><code><span class=\"line\"><span style=\"--0:#F97583;--1:#BF3441\">const</span>", html);

        // A terminal leaves its comments out of the code to copy; a shebang makes it a code frame.
        Assert.Contains("<figure class=\"code code-terminal\" data-language=\"bash\"><figcaption class=\"code-title\"></figcaption>", html);
        Assert.Contains("data-code=\"npm install\"", html);
        Assert.Contains("<figure class=\"code code-code\" data-language=\"bash\"><pre><code><span class=\"line\"><span style=\"--0:#99A0A6;--1:#616972\">#!/bin/bash</span>", html);
        Assert.Contains("<figure class=\"code code-none\" data-language=\"bash\"><pre>", html);
    }

    [Fact]
    public async Task LineAndInlineMarkers()
    {
        var html = await RenderAsync(Fence("js", "let a = 1;\nlet b = 2;\nlet c = 3;\nlet d = 4;", """{1} ins={2} del={"Gone":3} "c" /(d) =/"""));
        var lines = html.Split("<span class=\"line").Skip(1).ToArray();

        Assert.StartsWith(" mark\">", lines[0]);
        Assert.StartsWith(" ins\">", lines[1]);
        Assert.StartsWith(" del\" data-label=\"Gone\">", lines[2]);
        Assert.Contains("<mark><span style=", lines[2]);
        Assert.Contains("<mark><span style=\"--0:#E1E4E8;--1:#24292E\">d</span></mark><span style=", lines[3]);
    }

    [Fact]
    public async Task InlineMarkersAroundAnnotations()
    {
        // A marker across tokens of different styles and a twoslash hover is one element around them.
        var html = await RenderAsync("""<pre data-lang="ts" data-meta="&quot;a = b&quot;" data-twoslash="{&quot;code&quot;:&quot;let a = b;&quot;,&quot;nodes&quot;:[{&quot;type&quot;:&quot;hover&quot;,&quot;start&quot;:4,&quot;length&quot;:1,&quot;line&quot;:0,&quot;character&quot;:4,&quot;text&quot;:&quot;let a: any&quot;}]}"><code>let a = b;</code></pre>""", path: "/docs/a.html");

        Assert.Contains("<mark><span class=\"twoslash-hover\" tabindex=\"0\"><span style=", html);
        Assert.DoesNotContain("open-start", html);
    }

    [Fact]
    public async Task DiffSyntax()
    {
        var html = await RenderAsync(Fence("diff", "+added\n-removed\n kept", "lang=\"js\""));

        Assert.Contains("data-language=\"js\"", html);
        Assert.Contains("<span class=\"line ins\">", html);
        Assert.Contains("<span class=\"line del\">", html);
        Assert.Contains("data-code=\"added&#xA;removed&#xA;kept\"", html);
    }

    [Fact]
    public async Task CollapsedSectionsAndLineNumbers()
    {
        var html = await RenderAsync(Fence("py", "a = 1\nb = 2\nc = 3\nd = 4", "collapse={2-3} showLineNumbers startLineNumber=10"));

        Assert.Contains("<details class=\"code-section\"><summary>2 lines</summary><span class=\"line\"><span class=\"line-number\" aria-hidden=\"true\">11</span>", html);
        Assert.Contains("<span class=\"line-number\" aria-hidden=\"true\">13</span>", html);
    }

    [Fact]
    public async Task TwoslashPopups()
    {
        var twoslash = """{"code":"const answer = 42;\nanswer.toFixed();","nodes":[{"type":"hover","start":6,"length":6,"line":0,"character":6,"text":"const answer: 42","docs":"The **answer**.","tags":[["see","the question"]]},{"type":"error","start":25,"length":7,"line":1,"character":7,"code":2339,"text":"Nope.","level":"error"}]}""";
        var html = await RenderAsync($"<pre data-lang=\"ts\" data-meta=\"twoslash\" data-twoslash=\"{WebUtility.HtmlEncode(twoslash)}\"><code>const answer = 42;\nanswer.toFixed();</code></pre>", path: "/docs/a.html");

        // The type is highlighted as a block of its own, the documentation is Markdown.
        Assert.Contains("<span class=\"twoslash-hover\" tabindex=\"0\"><span style=\"--0:#79B8FF;--1:#005CC5\">answer</span><span class=\"twoslash-popup\" role=\"tooltip\"><figure class=\"code code-code\" data-language=\"ts\"><pre><code><span class=\"line\"><span style=\"--0:#F97583;--1:#BF3441\">const</span>", html);
        Assert.Contains("<p>The <strong>answer</strong>.</p>", html);
        Assert.Contains("<p class=\"twoslash-tag\"><b>@see</b> the question</p>", html);
        Assert.Contains("<span class=\"twoslash-error-underline\"><span style=", html);
        Assert.Contains("<span class=\"twoslash-error twoslash-error-error\">2339: Nope.</span>", html);
    }

    [Fact]
    public async Task UnknownLanguagesArePlainTextWithAWarning()
    {
        var html = await RenderAsync(Fence("nope", "a <b>") + Fence("nope", "c") + Fence("mermaid", "graph TD"));

        Assert.Contains("data-language=\"nope\"><pre><code><span class=\"line\"><span style=\"--0:#E1E4E8;--1:#24292E\">a &lt;b&gt;</span>", html);
        Assert.Contains(Messages, x => x.Level == LogLevel.Warning && x.Message == "no grammar for 'nope' (2 fences): rendered as plain text; add one with the option 'grammars' or map the language with 'aliases'");
    }

    [Fact]
    public async Task AliasesGrammarsThemesAndTemplates()
    {
        await Source.WriteTextFileAsync("/syntax/note.json", """{ "scopeName": "source.note", "patterns": [ { "match": "\\bTODO\\b", "name": "keyword.todo" } ] }""");
        await Source.WriteTextFileAsync("/themes/mine.json", """{ "name": "mine", "type": "dark", "colors": { "editor.background": "#000000", "editor.foreground": "#ffffff" }, "tokenColors": [ { "scope": "keyword", "settings": { "foreground": "#ff0000" } } ] }""");
        await Source.WriteTextFileAsync("/_includes/code.liquid", "<pre class=\"{{ code.language }}\">{% for line in code.lines %}{{ line.html }}|{% endfor %}</pre>\n");

        var html = await RenderAsync(Fence("todo", "a TODO b"), StepTestBase.With(
            ("themes", new NodeArray { NodeValue.Create("themes/mine.json") }),
            ("min-contrast", NodeValue.Create(0)),
            ("grammars", new NodeObject { ["note"] = NodeValue.Create("syntax/note.json") }),
            ("aliases", new NodeObject { ["todo"] = NodeValue.Create("note") }),
            ("templates", new NodeObject { ["block"] = NodeValue.Create("code.liquid") })));

        Assert.Contains("<pre class=\"todo\"><span style=\"--0:#FFFFFF\">a </span><span style=\"--0:#FF0000\">TODO</span><span style=\"--0:#FFFFFF\"> b</span>|</pre>", html);
    }

    [Fact]
    public async Task ColorsAreMadeReadable()
    {
        // github-light's comments (#6a737d): darker on its background for a contrast of 5.5, and darker still on a
        // marked line; with min-contrast: 0, the theme's color.
        Assert.Contains("--1:#616972", await RenderAsync(Fence("js", "// c")));
        Assert.Contains("--1:#4D545B", await RenderAsync(Fence("js", "// c", "{1}")));
        Assert.Contains("--1:#6A737D", await RenderAsync(Fence("js", "// c"), StepTestBase.With(("min-contrast", NodeValue.Create(0)))));
    }

    [Fact]
    public async Task Stylesheet()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("js", "let a;"));
        var result = await BuildCachedAsync(Highlight(StepTestBase.With(("stylesheet", NodeValue.Create("assets/code.css")))));
        var css = await StepTestBase.TextAsync(StepTestBase.Outputs(result["highlight"]).Single(x => x.Path.ToString() == "/assets/code.css"));

        Assert.Contains(":root .code { --code-background: #24292e;", css);
        Assert.Contains("@media (prefers-color-scheme: light)", css);
        Assert.Contains("color: var(--1, inherit)", css);
    }

    [Fact]
    public async Task TokensAreMemoized()
    {
        var workflow = Highlight();
        await Source.WriteTextFileAsync("/docs/a.md", Fence("js", "let a;") + Fence("js", "let b;"));
        await BuildCachedAsync(workflow);
        Assert.Contains(Messages, x => x.Message == "/docs/a.md: 2 fences, 2 tokenized, 0 from the cache");

        // The page changes: its new fence is tokenized, the other one comes from the cache.
        Messages.Clear();
        await Source.WriteTextFileAsync("/docs/a.md", Fence("js", "let a;") + Fence("js", "let c;"));
        await BuildCachedAsync(workflow);
        Assert.Contains(Messages, x => x.Message == "/docs/a.md: 2 fences, 1 tokenized, 1 from the cache");

        // Nothing changes: the unit is replayed.
        var result = await BuildCachedAsync(workflow);
        Assert.Empty(Executed(result, "highlight"));
    }

    [Fact]
    public void InvalidOptions()
    {
        var e = Assert.Throws<WorkflowValidationException>(() => WorkflowGraph.Build(Highlight(StepTestBase.With(("markers", new NodeObject { ["mark"] = NodeValue.Create("#fff") })))));
        Assert.Equal("option 'markers' of step 'highlight': 'mark' must be mark, ins or del, with a color per theme (2)", e.Errors.Single().Message);

        e = Assert.Throws<WorkflowValidationException>(() => WorkflowGraph.Build(Highlight(StepTestBase.With(("templates", new NodeObject { ["frame"] = NodeValue.Create("x") })))));
        Assert.Equal("option 'templates' of step 'highlight' has an unknown template 'frame': block or popup", e.Errors.Single().Message);
    }
}
