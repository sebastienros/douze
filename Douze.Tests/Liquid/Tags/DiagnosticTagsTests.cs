using Douze.FileSystems;
using Douze.Nodes;
using Douze.Progress;
using Douze.Tests.Workflow.Cache;
using Douze.Tests.Workflow.Steps;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Liquid.Tags;

public class DiagnosticTagsTests : CacheTestBase
{
    private static WorkflowDefinition Render(NodeObject? liquid = null, NodeObject? frontmatter = null) => StepTestBase.Workflow(
        StepTestBase.Step("folder-data", "data", inputs: ["**/*.yml"], with: StepTestBase.With(("scope", NodeValue.Create("folder")))),
        StepTestBase.Step("pages", "frontmatter", needs: ["folder-data"], inputs: ["**/*.md", "**/*.html"], with: frontmatter),
        StepTestBase.Step("render", "liquid", needs: ["pages"], with: liquid));

    [Fact]
    public async Task ErrorTagFailsTheUnitWithThePageAndThePosition()
    {
        await Source.WriteTextFileAsync("/a.md", "---\nref: Foo\n---\nline 1\nline 2 {% error \"unknown API reference \" | append: ref %}");
        await Source.WriteTextFileAsync("/b.md", "fine");

        var result = await BuildAsync(Render());

        var error = Assert.Single(result.Errors);
        Assert.Equal("error: render: /a.md(2,8): unknown API reference Foo", error.ToString());
        Assert.Null(error.Exception);
    }

    [Fact]
    public async Task ErrorTagInALayoutNamesTheLayout()
    {
        await Source.WriteTextFileAsync("/_includes/base.html", "<main>\n{% error \"no title\" %}</main>");
        await Source.WriteTextFileAsync("/_content/a.md", "---\nlayout: base.html\n---\nText");

        var result = await BuildAsync();

        Assert.Equal("error: layout: /a/index.html: /_includes/base.html(2,1): no title", Assert.Single(result.Errors).ToString());
    }

    [Fact]
    public async Task WarnTagLogsAndRenders()
    {
        await Source.WriteTextFileAsync("/a.md", "before {% warn \"deprecated\" %}after");

        var result = await BuildCachedAsync(Render());

        Assert.Equal("before after", await StepTestBase.TextAsync(StepTestBase.Outputs(result["render"]).Single()));
        Assert.Contains((LogLevel.Warning, "/a.md(1,8): deprecated"), Messages);
    }

    [Fact]
    public async Task RequiredKeysMustResolve()
    {
        await Source.WriteTextFileAsync("/docs/docs.yml", "description: Docs");
        await Source.WriteTextFileAsync("/docs/a.md", "---\ntitle: A\n---\nA");
        await Source.WriteTextFileAsync("/other/b.md", "---\ntitle: B\n---\nB");
        await Source.WriteTextFileAsync("/other/c.md", "C");

        var result = await BuildAsync(Render(frontmatter: StepTestBase.With(("require", new NodeArray { NodeValue.Create("title"), NodeValue.Create("description") }))));

        // The folder data of docs/ provides the description of a.md.
        Assert.Equal([
            "error: pages: /other/b.md: required key 'description' is missing",
            "error: pages: /other/c.md: required keys 'title', 'description' are missing",
        ], result.Errors.Select(x => x.ToString()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task StrictMakesAnUndefinedVariableAnError()
    {
        await Source.WriteTextFileAsync("/a.md", "---\ntitle: A\n---\n{{ title }} {{ titel }}");

        var strict = await BuildAsync(Render(liquid: StepTestBase.With(("strict", NodeValue.Create(true)))));

        Assert.Equal("error: render: /a.md: undefined variable 'titel'", Assert.Single(strict.Errors).ToString());

        var lenient = await BuildCachedAsync(Render(), cache: false);
        Assert.Equal("A ", await StepTestBase.TextAsync(StepTestBase.Outputs(lenient["render"]).Single()));
    }

    [Fact]
    public async Task StrictAcceptsAssignedAndLoopVariables()
    {
        await Source.WriteTextFileAsync("/a.md", "---\nitems: [1, 2]\n---\n{% assign x = 'x' %}{{ x }}{% for i in items %}{{ i }}{% endfor %}");

        var result = await BuildCachedAsync(Render(liquid: StepTestBase.With(("strict", NodeValue.Create(true)))), cache: false);

        Assert.Equal("x12", await StepTestBase.TextAsync(StepTestBase.Outputs(result["render"]).Single()));
    }
}
