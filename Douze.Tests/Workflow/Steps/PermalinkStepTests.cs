using Douze.FileSystems;
using Douze.Nodes;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow.Steps;

public class PermalinkStepTests : StepTestBase
{
    private static WorkflowDefinition Permalink(string? root = null) => Workflow(
        Step("pages", "frontmatter", inputs: ["**"]),
        Step("permalink", "permalink", needs: ["pages"], with: root == null ? null : With(("root", NodeValue.Create(root)))));

    private static string[] Paths(BuildResult result) => [.. result.Steps.Single(x => x.Node.Definition.Name == "permalink").Results.SelectMany(x => x.Outputs).Select(x => x.Path.FullName).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task RootStripped()
    {
        await Source.WriteTextFileAsync("/_content/index.html", "home");
        await Source.WriteTextFileAsync("/_content/blog/post.html", "post");
        await Source.WriteTextFileAsync("/_content/images/a.png", "png");

        var result = await BuildSuccessfullyAsync(Permalink());

        // The content folder is the default root; files other than HTML keep their name.
        Assert.Equal(["/blog/post/index.html", "/images/a.png", "/index.html"], Paths(result));

        var post = Outputs(result["permalink"]).Single(x => x.Path == "/blog/post/index.html");
        Assert.Equal("/blog/post/index.html", post.Metadata.Own["url"]!.GetValue<string>());
        Assert.Equal("/_content/blog/", post.Metadata.Directory.FullName);
        Assert.Equal("/_content/blog/post.html", post.SourcePath?.FullName);
    }

    [Fact]
    public async Task CustomExtensionKept()
    {
        await Source.WriteTextFileAsync("/site/api.html", "---\npermalink: api/posts.json\n---\n{}");

        var result = await BuildSuccessfullyAsync(Permalink(root: "site"));

        var file = Assert.Single(Outputs(result["permalink"]));
        Assert.Equal("/api/posts.json", file.Path.FullName);
        Assert.Equal("application/json", file.EffectiveContentType);
    }

    [Fact]
    public async Task PermalinkFalseDrops()
    {
        await Source.WriteTextFileAsync("/draft.html", "---\npermalink: false\n---\ndraft");
        await Source.WriteTextFileAsync("/index.html", "home");

        var result = await BuildSuccessfullyAsync(Permalink());

        Assert.Equal(["/index.html"], Paths(result));
    }

    [Fact]
    public async Task Templated()
    {
        await Source.WriteTextFileAsync("/_data/blog.yml", "prefix: articles");
        await Source.WriteTextFileAsync("/post.html", "---\ntitle: My Post!\npermalink: \"{{ blog.prefix }}/{{ title | slug }}\"\n---\npost");

        var result = await BuildSuccessfullyAsync(Workflow(
            Step("data", "data", inputs: ["_data/**"], with: With(("scope", NodeValue.Create("site")))),
            Step("pages", "frontmatter", inputs: ["*.html"], needs: ["data"]),
            Step("permalink", "permalink", needs: ["pages"])));

        Assert.Equal(["/articles/my-post/index.html"], Paths(result));

        // The permalink read site data: the unit depends on the data file.
        Assert.Contains("/_data/blog.yml", result["permalink"].Results.Single().Dependencies.DataFiles.Select(x => x.FullName));
    }
}
