using Douze.FileSystems;
using Douze.Nodes;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow.Steps;

public class ForkStepTests : StepTestBase
{
    private static WorkflowDefinition Twins(NodeObject with) => Workflow(
        Step("pages", "frontmatter", inputs: ["**"]),
        Step("permalink", "permalink", needs: ["pages"]),
        Step("render", "liquid", needs: ["permalink"]),
        Step("twins", "fork", needs: ["render"], match: ["**/*.md"], with: with),
        // The markdown step of this phase does not read 'markdown: false' yet: it is given the pages only.
        Step("markdown", "markdown", needs: ["twins"], match: ["**/index.md"]),
        Step("write", "write", needs: ["markdown"]));

    private static NodeObject TwinOptions(string? content = null, bool? stripRegions = null)
    {
        var with = With(
            ("permalink", NodeValue.Create("{{ url | replace: 'index.html', 'page.md' }}")),
            ("data", YamlHelper.Parse("{ markdown: false, liquid: false, twin: true }")));

        if (content != null)
        {
            with["content"] = NodeValue.Create(content);
        }

        if (stripRegions != null)
        {
            with["strip-regions"] = NodeValue.Create(stripRegions.Value);
        }

        return with;
    }

    [Fact]
    public async Task EmitsTheOriginalAndACopy()
    {
        await Source.WriteTextFileAsync("/docs/a.md", "---\ntitle: A\n---\n# {{ title }}");

        var result = await BuildSuccessfullyAsync(Twins(TwinOptions()));

        var outputs = Outputs(result["twins"]);
        Assert.Equal(["/docs/a/index.md", "/docs/a/page.md"], outputs.Select(x => x.Path.FullName));

        var original = Assert.Single(outputs, x => x.Metadata.Own["twin"] == null);
        var twin = Assert.Single(outputs, x => x.Metadata.Own["twin"] != null);

        Assert.Same(Outputs(result["render"]).Single(), original);
        Assert.Same(original.Content, twin.Content);
        Assert.Equal("/docs/a/page.md", twin.Metadata.Own["url"]!.GetValue<string>());
        Assert.Equal("A", twin.Metadata.Own["title"]!.GetValue<string>());
        Assert.False(twin.Metadata.Own["markdown"]!.GetValue<bool>());
    }

    [Fact]
    public async Task SkipsTheFilesOfACondition()
    {
        await Source.WriteTextFileAsync("/docs/a.md", "---\ntitle: A\ncopy: false\n---\nA");
        await Source.WriteTextFileAsync("/docs/b.md", "---\ntitle: B\n---\nB");

        var result = await BuildSuccessfullyAsync(Twins(With(("permalink", NodeValue.Create("{{ url | replace: '/index.html', '/page.md' }}")), ("skip", NodeValue.Create("copy == false")))));

        Assert.Equal(["/docs/a/index.md", "/docs/b/index.md", "/docs/b/page.md"], Outputs(result["twins"]).Select(x => x.Path.FullName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TwinIsWrittenAsMarkdownNextToThePage()
    {
        await Source.WriteTextFileAsync("/docs/a.md", "---\ntitle: A\n---\n# {{ title }}");

        // The twin is written next to the page, as Markdown after Liquid.
        await BuildSuccessfullyAsync(Twins(TwinOptions()));

        Assert.Equal("<h1>A</h1>\n", await Output.ReadAllTextAsync("/docs/a/index.html"));
        Assert.Equal("# A", await Output.ReadAllTextAsync("/docs/a/page.md"));
    }

    [Fact]
    public async Task ContentTemplateAndRegionMarkers()
    {
        await Source.WriteTextFileAsync("/docs/a.md", "---\ntitle: A & B\n---\nintro\n\u001E\n<div>component</div>\n\u001E\nend");

        var twins = TwinOptions(content: "---\ntitle: {{ title }}\n---\n{{ content }}");
        twins["permalink"] = NodeValue.Create("/twins/{{ title | slug }}.md");

        var result = await BuildSuccessfullyAsync(Workflow(
            Step("pages", "frontmatter", inputs: ["**"]),
            Step("twins", "fork", needs: ["pages"], with: twins)));

        var twin = Outputs(result["twins"]).Single(x => x.Path.FullName.StartsWith("/twins/", StringComparison.Ordinal));
        Assert.Equal("---\ntitle: A & B\n---\nintro\n<div>component</div>\nend", await TextAsync(twin));
    }

    [Fact]
    public async Task RegionMarkersKeptOnRequest()
    {
        await Source.WriteTextFileAsync("/a.md", "x\n\u001E\ny");

        var result = await BuildSuccessfullyAsync(Workflow(
            Step("pages", "frontmatter", inputs: ["**"]),
            Step("twins", "fork", needs: ["pages"], with: With(("permalink", NodeValue.Create("/copy.md")), ("strip-regions", NodeValue.Create(false))))));

        Assert.All(Outputs(result["twins"]), x => Assert.Same(Outputs(result["pages"]).Single().Content, x.Content));
    }

    [Fact]
    public void StripRegionsRemovesMarkerLinesOnly()
    {
        Assert.Equal("a\r\nb\nc \u001E\n", Douze.Workflow.Steps.ForkStep.StripRegions("a\r\n\u001E\r\nb\n\u001E\nc \u001E\n\u001E"));
        Assert.Equal("plain", Douze.Workflow.Steps.ForkStep.StripRegions("plain"));
    }

    [Fact]
    public async Task FolderPermalinkGetsAnIndex()
    {
        await Source.WriteTextFileAsync("/a.html", "a");

        var with = With(("permalink", NodeValue.Create("copies/{{ 'a' }}")));
        var result = await BuildSuccessfullyAsync(Workflow(Step("pages", "frontmatter", inputs: ["**"]), Step("copy", "fork", needs: ["pages"], with: with)));

        Assert.Equal(["/a.html", "/copies/a/index.html"], Outputs(result["copy"]).Select(x => x.Path.FullName));
    }
}
