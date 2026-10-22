using Douze.FileSystems;
using Douze.Liquid;
using Douze.Nodes;
using Douze.Progress;
using Douze.Tracing;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow.Steps;

public class LiquidStepTests : StepTestBase
{
    private static WorkflowDefinition Render() => Workflow(
        Step("pages", "frontmatter", inputs: ["**"]),
        Step("render", "liquid", needs: ["pages"]));

    [Fact]
    public async Task IncludeThroughTheFileProviderRecordsADependency()
    {
        await Source.WriteTextFileAsync("/_includes/greeting.liquid", "Hello {{ name }}");
        await Source.WriteTextFileAsync("/index.html", "---\nname: Douze\n---\n{% include 'greeting' %}!");

        var result = await BuildSuccessfullyAsync(Workflow(
            Step("pages", "frontmatter", inputs: ["*.html"]),
            Step("render", "liquid", needs: ["pages"])));

        var unit = result["render"].Results.Single();
        Assert.Equal("Hello Douze!", await TextAsync(unit.Files.Single()));
        Assert.Equal(["/_includes/greeting.liquid"], unit.Dependencies.Templates.Select(x => x.Path.FullName));
    }

    [Fact]
    public async Task ParseErrorHasLineAndColumn()
    {
        await Source.WriteTextFileAsync("/index.html", "line 1\nline 2 {% if %}");

        var result = await BuildAsync(Render());

        var error = Assert.Single(result.Errors);
        Assert.Equal("/index.html", error.File?.FullName);
        Assert.Equal(2, error.Location?.Line);
        Assert.NotNull(error.Location?.Column);
        Assert.StartsWith("error: render: /index.html(2,", error.ToString());
        Assert.Null(error.Exception);
    }

    [Fact]
    public async Task CaptureLandsInOwn()
    {
        await Source.WriteTextFileAsync("/index.html", "body{% capture footer %}<b>{{ 1 | plus: 1 }}</b>{% endcapture %}");

        var result = await BuildSuccessfullyAsync(Render());

        var file = Assert.Single(Outputs(result["render"]));
        Assert.Equal("body", await TextAsync(file));
        Assert.Equal("<b>2</b>", file.Metadata.Own["footer"]!.GetValue<string>());
    }

    [Fact]
    public async Task LiquidFalsePassesThrough()
    {
        await Source.WriteTextFileAsync("/a.md", "---\nliquid: false\n---\n{{ broken");
        await Source.WriteTextFileAsync("/b.md", "{{ 1 | plus: 1 }}");

        var result = await BuildSuccessfullyAsync(Render());

        // Markdown files are templates by default; liquid: false keeps the content, even when it is not valid Liquid.
        var files = Outputs(result["render"]).ToDictionary(x => x.Path.FullName);
        Assert.Equal("{{ broken", await TextAsync(files["/a.md"]));
        Assert.Equal("2", await TextAsync(files["/b.md"]));
    }

    [Fact]
    public async Task TemplateParsedOnceForTwoFilesWithTheSameContent()
    {
        await Source.WriteTextFileAsync("/a.html", "---\ntitle: A\n---\n<h1>{{ title }}</h1>");
        await Source.WriteTextFileAsync("/b.html", "---\ntitle: B\n---\n<h1>{{ title }}</h1>");

        TemplateLoader? loader = null;
        var definition = Workflow(Step("pages", "frontmatter", inputs: ["**"]), Step("render", "liquid", needs: ["pages"]), Step("spy", "spy", needs: ["render"]));
        var graph = WorkflowGraph.Build(definition, d => d.Name != "spy" ? StepFactory.Create(d) : new FakeMapStep(d)
        {
            Process = (f, c, _) =>
            {
                loader = c.Build.Templates;
                return new(FileList.Of(f));
            },
        });

        var result = await SiteBuilder.BuildAsync(graph, await SiteOptions.DetectAsync(Source, Output, null, null), new BuildOptions { KeepFiles = true, View = SilentProgressView.Discard, Trace = NullTraceRecorder.Instance });

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(["<h1>A</h1>", "<h1>B</h1>"], await Task.WhenAll(Outputs(result["render"]).Select(TextAsync)));
        Assert.Equal(1, loader!.ParsedCount);
    }

    [Fact]
    public async Task GroupedModeWithTemplateAndPermalink()
    {
        await Source.WriteTextFileAsync("/_includes/package.liquid", "{{ group.key }}:{% for f in group.files %} {{ f.data.package.name }}@{{ f.data.package.version }}{% endfor %} ({{ site.title }})");
        await Source.WriteTextFileAsync("/_data/site.yml", "title: API");
        await Source.WriteTextFileAsync("/pkgs/redis.json", """{ "name": "redis", "version": "1.0", "group": "data" }""");
        await Source.WriteTextFileAsync("/pkgs/pg.json", """{ "name": "pg", "version": "2.0", "group": "data" }""");
        await Source.WriteTextFileAsync("/pkgs/web.json", """{ "name": "web", "version": "3.0", "group": "hosting" }""");

        var result = await BuildSuccessfullyAsync(Workflow(
            Step("data", "data", inputs: ["_data/**"], with: With(("scope", NodeValue.Create("site")))),
            Step("packages", "parse", inputs: ["pkgs/*.json"], with: With(("as", NodeValue.Create("package")))),
            Step("api", "liquid", needs: ["packages", "data"], groupBy: "{{ data.package.group }}", with: With(
                ("template", NodeValue.Create("package.liquid")),
                ("permalink", NodeValue.Create("api/{{ group.key }}/")),
                ("data", new NodeObject { ["kind"] = NodeValue.Create("api") })))));

        var outputs = Outputs(result["api"]);
        Assert.Equal(["/api/data/index.html", "/api/hosting/index.html"], outputs.Select(x => x.Path.FullName));
        Assert.Equal("data: pg@2.0 redis@1.0 (API)", await TextAsync(outputs[0]));
        Assert.Equal("api", outputs[0].Metadata.Own["kind"]!.GetValue<string>());
        Assert.Equal("/api/data/index.html", outputs[0].Metadata.Own["url"]!.GetValue<string>());
        Assert.Equal(["/pkgs/pg.json", "/pkgs/redis.json"], outputs[0].Provenance.Inputs.Select(x => x.Path.FullName));

        var missing = Assert.Throws<WorkflowValidationException>(() => WorkflowGraph.Build(Workflow(
            Step("api", "liquid", inputs: ["**"], with: With(("template", NodeValue.Create("package.liquid")))))));
        Assert.Equal("step 'api' uses 'liquid' with 'template' but 'permalink' is missing", missing.Errors.Single().Message);
    }

    [Fact]
    public async Task OutputTemplatesRenderEndToEnd()
    {
        await Source.WriteTextFileAsync("/_data/layout.yml", "page.html");
        await Source.WriteTextFileAsync("/_includes/page.html", "<html>{{ content }}</html>");
        await Source.WriteTextFileAsync("/_content/a.md", "---\ndate: 2020-01-01\n---\na");
        await Source.WriteTextFileAsync("/_content/b.md", "---\ndate: 2020-01-02\n---\nb");
        await Source.WriteTextFileAsync("/_content/sitemap.xml.liquid", "---\npermalink: /sitemap.xml\n---\n<urlset>{% for p in collections.all %}<url>{{ p.data.url | replace: 'index.html', '' }}</url>{% endfor %}</urlset>");
        await Source.WriteTextFileAsync("/_content/robots.txt.liquid", "Sitemap: {{ '/sitemap.xml' | absolute_url }}");
        Site = await SiteOptions.DetectAsync(Source, Output, null, null, new Douze.Workflow.Yaml.SiteSettings { Url = "https://example.com" });

        var result = await BuildSuccessfullyAsync();

        // Not a page: no layout, not in the collections.
        Assert.Equal("<urlset><url>/a/</url><url>/b/</url></urlset>", await Output.ReadAllTextAsync("/sitemap.xml"));
        Assert.Equal("Sitemap: https://example.com/sitemap.xml", await Output.ReadAllTextAsync("/robots.txt"));
        Assert.Equal("application/xml", Outputs(result["render"]).Single(x => x.Path == "/sitemap.xml").EffectiveContentType);
    }

    // Fence protection.

    [Fact]
    public async Task FencesOfAMarkdownFileAreNotLiquid()
    {
        await Source.WriteTextFileAsync("/a.md", "{{ 1 | plus: 1 }}\n\n```yaml\nrun: ${{ matrix.os }}\n{% if %}\n```\n\n- item\n\n  ~~~~\n  {{ x }}\n  ~~~~\n\n{{ 2 | plus: 2 }}\n");
        await Source.WriteTextFileAsync("/b.html", "```\n{{ 1 | plus: 1 }}\n```");

        var result = await BuildSuccessfullyAsync(Render());

        var files = Outputs(result["render"]).ToDictionary(x => x.Path.FullName);
        Assert.Equal("2\n\n```yaml\nrun: ${{ matrix.os }}\n{% if %}\n```\n\n- item\n\n  ~~~~\n  {{ x }}\n  ~~~~\n\n4\n", await TextAsync(files["/a.md"]));

        // An HTML file has no fences.
        Assert.Equal("```\n2\n```", await TextAsync(files["/b.html"]));
    }

    [Fact]
    public async Task InlineCodeAndUnclosedFences()
    {
        await Source.WriteTextFileAsync("/a.md", "```{{ 1 | plus: 1 }}``` and `{{ 2 | plus: 2 }}`\n\n````\n{{ open }}\n```\nstill code\n");

        var result = await BuildSuccessfullyAsync(Render());

        // A backtick fence's info string has no backtick, so the first line is inline code; a fence that is not closed
        // runs to the end, and a shorter fence does not close it.
        Assert.Equal("```2``` and `4`\n\n````\n{{ open }}\n```\nstill code\n", await TextAsync(Outputs(result["render"]).Single()));
    }

    [Fact]
    public async Task ErrorAfterAFenceHasTheOriginalLine()
    {
        await Source.WriteTextFileAsync("/a.md", "```\n{{ a }}\n{{ b }}\n```\n\n{% if %}");

        var result = await BuildAsync(Render());

        var error = Assert.Single(result.Errors);
        Assert.Equal(6, error.Location?.Line);
    }

    [Fact]
    public async Task ProtectNoneRendersFences()
    {
        await Source.WriteTextFileAsync("/a.md", "```\n{{ 1 | plus: 1 }}\n```");

        var result = await BuildSuccessfullyAsync(Workflow(
            Step("pages", "frontmatter", inputs: ["**"]),
            Step("render", "liquid", needs: ["pages"], with: With(("protect", NodeValue.Create("none"))))));

        Assert.Equal("```\n2\n```", await TextAsync(Outputs(result["render"]).Single()));

        var invalid = Assert.Throws<WorkflowValidationException>(() => WorkflowGraph.Build(Workflow(
            Step("render", "liquid", inputs: ["**"], with: With(("protect", NodeValue.Create("code")))))));
        Assert.Equal("option 'protect' of step 'render' must be 'fences' or 'none', not 'code'", invalid.Errors.Single().Message);
    }

    [Fact]
    public async Task CapturedFencesAreRestored()
    {
        await Source.WriteTextFileAsync("/a.md", "{% capture sample %}\n```\n{{ x }}\n```\n{% endcapture %}");

        var result = await BuildSuccessfullyAsync(Render());

        Assert.Equal("\n```\n{{ x }}\n```\n", Outputs(result["render"]).Single().Metadata.Own["sample"]!.GetValue<string>());
    }
}
