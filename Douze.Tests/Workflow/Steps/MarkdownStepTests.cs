using Douze.FileSystems;
using Douze.Markdown;
using Douze.Nodes;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow.Steps;

public class MarkdownStepTests : StepTestBase
{
    [Fact]
    public async Task ExtensionRenamed()
    {
        await Source.WriteTextFileAsync("/blog/post1.md", "**Hello**");

        var result = await BuildSuccessfullyAsync(Workflow(Step("markdown", "markdown", inputs: ["**"])));

        var file = Assert.Single(Outputs(result["markdown"]));
        Assert.Equal("/blog/post1.html", file.Path.FullName);
        Assert.Equal("<p><strong>Hello</strong></p>\n", await TextAsync(file));
        Assert.Equal("/blog/", file.Metadata.Directory.FullName);
    }

    [Fact]
    public async Task ContentTypeAndExtensions()
    {
        await Source.WriteTextFileAsync("/a.md", "| a |\n|---|\n| 1 |");

        var bare = await BuildSuccessfullyAsync(Workflow(Step("markdown", "markdown", inputs: ["**"])));
        var bareFile = Assert.Single(Outputs(bare["markdown"]));
        Assert.Equal("text/html", bareFile.EffectiveContentType);
        Assert.DoesNotContain("<table>", await TextAsync(bareFile));

        Site = null;
        var advanced = await BuildSuccessfullyAsync(Workflow(Step("markdown", "markdown", inputs: ["**"], with: With(("extensions", NodeValue.Create("advanced"))))));
        Assert.Contains("<table>", await TextAsync(Assert.Single(Outputs(advanced["markdown"]))));

        var invalid = Assert.Throws<WorkflowValidationException>(() => WorkflowGraph.Build(Workflow(Step("markdown", "markdown", inputs: ["**"], with: With(("extensions", NodeValue.Create(3)))))));
        Assert.Equal("option 'extensions' of step 'markdown' must be a string or a list of strings", invalid.Errors.Single().Message);
    }

    [Fact]
    public async Task UsesTheSitePipeline()
    {
        await Source.WriteTextFileAsync("/a.md", "| a |\n|---|\n| 1 |\n\n:::tip\nText\n:::");
        Site = await SiteOptions.DetectAsync(Source, Output, null, null, markdown: new MarkdownOptions());

        var result = await BuildSuccessfullyAsync(Workflow(Step("markdown", "markdown", inputs: ["**"])));
        var html = await TextAsync(Assert.Single(Outputs(result["markdown"])));

        Assert.Contains("<table>", html);
        Assert.Contains("<aside aria-label=\"Tip\" class=\"aside aside--tip\">", html);
    }

    [Fact]
    public async Task ExtensionsOverrideTheSitePipelineAndKeepItsOtherOptions()
    {
        await Source.WriteTextFileAsync("/a.md", "| a |\n|---|\n| 1 |\n\n```js\nx\n```");
        Site = await SiteOptions.DetectAsync(Source, Output, null, null, markdown: new MarkdownOptions { CodeClassPrefix = "lang-" });

        var result = await BuildSuccessfullyAsync(Workflow(Step("markdown", "markdown", inputs: ["**"], with: With(("extensions", NodeValue.Create("none"))))));
        var html = await TextAsync(Assert.Single(Outputs(result["markdown"])));

        Assert.DoesNotContain("<table>", html);
        Assert.Contains("<pre data-lang=\"js\"><code class=\"lang-js\">", html);
    }

    [Fact]
    public void UnknownExtensionOption()
    {
        var error = Assert.Throws<WorkflowValidationException>(() => WorkflowGraph.Build(Workflow(Step("markdown", "markdown", inputs: ["**"], with: With(("extensions", new NodeArray { NodeValue.Create("tabels") }))))));

        Assert.Equal("option 'extensions' of step 'markdown': unknown Markdown extension 'tabels'; did you mean 'pipetables'?", error.Errors.Single().Message);
    }

    [Fact]
    public async Task MarkdownFalsePassesThrough()
    {
        await Source.WriteTextFileAsync("/docs/_data.yml", "markdown: false");
        await Source.WriteTextFileAsync("/docs/a.md", "**raw**");
        await Source.WriteTextFileAsync("/b.md", "---\nmarkdown: false\n---\n**raw**");
        await Source.WriteTextFileAsync("/c.md", "**rendered**");

        var result = await BuildSuccessfullyAsync(Workflow(
            Step("folder-data", "data", inputs: ["**/*.yml"], with: With(("scope", NodeValue.Create("folder")))),
            Step("pages", "frontmatter", inputs: ["**/*.md"], needs: ["folder-data"]),
            Step("markdown", "markdown", needs: ["pages"])));

        var files = Outputs(result["markdown"]);
        Assert.Equal(["/b.md", "/c.html", "/docs/a.md"], files.Select(x => x.Path.FullName));
        Assert.Equal("**raw**", await TextAsync(files[0]));
        Assert.Equal("<p><strong>rendered</strong></p>\n", await TextAsync(files[1]));
        Assert.Equal("**raw**", await TextAsync(files[2]));
    }

    [Fact]
    public async Task LinksAreRewrittenFromTheSourceFolder()
    {
        await Source.WriteTextFileAsync("/_content/docs/a.md", "[b](b.md) [c](c.md)");
        await Source.WriteTextFileAsync("/_content/docs/b.md", "B");

        var result = await BuildSuccessfullyAsync(Workflow(
            Step("pages", "frontmatter", inputs: ["_content/**"]),
            Step("permalink", "permalink", needs: ["pages"], with: With(("root", NodeValue.Create("_content")))),
            Step("markdown", "markdown", needs: ["permalink"], match: ["docs/a/**"])));

        var unit = result["markdown"].Results.Single(x => x.Files.Any(f => f.Path.FullName == "/docs/a/index.html"));
        Assert.Equal("<p><a href=\"/docs/b/\">b</a> <a href=\"c.md\">c</a></p>\n", await TextAsync(unit.Files.Single()));
        Assert.Contains((new FPath("/_content/docs/c.md"), DependencySet.Absent), unit.Dependencies.Templates);
    }
}
