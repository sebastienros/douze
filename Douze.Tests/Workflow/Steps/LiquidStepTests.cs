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
        Assert.Equal("Hello Douze!", await TextAsync(unit.Outputs.Single()));
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

        var result = await SiteBuilder.BuildAsync(graph, await SiteOptions.DetectAsync(Source, Output, null, null), new BuildOptions { View = SilentProgressView.Discard, Trace = NullTraceRecorder.Instance });

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
}
