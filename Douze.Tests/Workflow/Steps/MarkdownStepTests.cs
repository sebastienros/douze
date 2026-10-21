using Douze.FileSystems;
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
}
