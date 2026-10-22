using Douze.FileSystems;
using Douze.Nodes;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow.Steps;

public class ReplaceStepTests : StepTestBase
{
    private static WorkflowDefinition Replace(NodeObject with) => Workflow(
        Step("data", "data", inputs: ["_data/**"], with: With(("scope", NodeValue.Create("site")))),
        Step("pages", "frontmatter", inputs: ["docs/**"], needs: ["data"]),
        Step("versions", "replace", needs: ["pages"], with: with));

    private static NodeObject Tokens(params (string Token, string Value)[] tokens)
    {
        var map = new NodeObject();

        foreach (var (token, value) in tokens)
        {
            map[token] = NodeValue.Create(value);
        }

        return With(("tokens", map));
    }

    [Fact]
    public async Task ReplacesInProseAndFences()
    {
        await Source.WriteTextFileAsync("/_data/versions.yml", "current: 1.2.3\npreview: 2.0.0-rc.1");
        await Source.WriteTextFileAsync("/docs/install.md", "---\ntitle: Install\n---\nUse %VERSION% or %PREVIEW%.\n\n```sh\ndotnet add package X --version %VERSION%\n```\n");

        var result = await BuildSuccessfullyAsync(Replace(Tokens(("%VERSION%", "{{ versions.current }}"), ("%PREVIEW%", "{{ versions.preview }} ({{ title }})"))));

        Assert.Equal(
            "Use 1.2.3 or 2.0.0-rc.1 (Install).\n\n```sh\ndotnet add package X --version 1.2.3\n```\n",
            await TextAsync(Outputs(result["versions"]).Single()));
    }

    [Fact]
    public async Task NoOccurrencePassesTheFileThrough()
    {
        await Source.WriteTextFileAsync("/_data/versions.yml", "current: 1.2.3");
        await Source.WriteTextFileAsync("/docs/a.md", "no token here");

        var result = await BuildSuccessfullyAsync(Replace(Tokens(("%VERSION%", "{{ versions.current }}"))));

        var unit = result["versions"].Results.Single();
        Assert.Same(Outputs(result["pages"]).Single(), unit.Files.Single());

        // The value is not rendered: the file does not depend on the data.
        Assert.Empty(unit.Dependencies.Lookups);
    }

    [Fact]
    public async Task ValueDependsOnTheDataItReads()
    {
        await Source.WriteTextFileAsync("/_data/versions.yml", "current: 1.2.3\nother: 9");
        await Source.WriteTextFileAsync("/docs/a.md", "v%VERSION%");

        var result = await BuildSuccessfullyAsync(Replace(Tokens(("%VERSION%", "{{ versions.current }}"))));

        var lookup = Assert.Single(result["versions"].Results.Single().Dependencies.Lookups);
        Assert.Equal("versions", lookup.Key);
        Assert.Equal(["current"], lookup.Access!.Members.Select(x => x.Key));
    }

    [Fact]
    public async Task SkipLeavesFilesAsTheyAre()
    {
        await Source.WriteTextFileAsync("/docs/a.md", "%TOKEN%");
        await Source.WriteTextFileAsync("/docs/literal.md", "%TOKEN%");

        var with = Tokens(("%TOKEN%", "value"));
        with["skip"] = NodeValue.Create("**/literal.md");

        var result = await BuildSuccessfullyAsync(Replace(with));

        Assert.Equal("value", await TextAsync(Outputs(result["versions"]).Single(x => x.Path == "/docs/a.md")));
        Assert.Equal("%TOKEN%", await TextAsync(Outputs(result["versions"]).Single(x => x.Path == "/docs/literal.md")));
    }

    [Fact]
    public async Task TokensAreRequired()
    {
        await Source.WriteTextFileAsync("/docs/a.md", "a");

        var error = await Assert.ThrowsAsync<WorkflowValidationException>(() => BuildAsync(Replace(new NodeObject())));

        Assert.Contains("step 'versions' needs the option 'tokens'", error.Message);
    }
}
