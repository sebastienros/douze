using Douze.FileSystems;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow.Steps;

public class WriteStepTests : StepTestBase
{
    [Fact]
    public async Task DuplicateOutputErrorText()
    {
        await Source.WriteTextFileAsync("/_content/a.md", "markdown");
        await Source.WriteTextFileAsync("/_content/a.html", "html");

        var result = await BuildAsync();

        var error = Assert.Single(result.Errors);
        Assert.Equal("write", error.Step);
        Assert.Equal("output '/a/index.html' is produced by both '/_content/a.html' and '/_content/a.md'", error.Message);
    }

    [Fact]
    public async Task DirectoriesCreated()
    {
        await Source.WriteTextFileAsync("/blog/2020/post.md", "post");
        await Source.WriteTextFileAsync("/images/logo.png", "png");

        var result = await BuildSuccessfullyAsync();

        Assert.True((await Output.GetDirectoryEntryAsync("/blog/2020/post/")).Exists);
        Assert.Equal("<p>post</p>\n", await Output.ReadAllTextAsync("/blog/2020/post/index.html"));

        // Files no step claims are written as they are.
        Assert.Equal("png", await Output.ReadAllTextAsync("/images/logo.png"));
        Assert.Equal(["/blog/2020/post/index.html", "/images/logo.png"], result.Outputs.Select(x => x.Path.FullName));
    }
}
