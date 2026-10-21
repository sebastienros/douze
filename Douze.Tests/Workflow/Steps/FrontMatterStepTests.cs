using Douze.FileSystems;
using Douze.Nodes;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow.Steps;

public class FrontMatterStepTests : StepTestBase
{
    private static WorkflowDefinition Pages() => Workflow(Step("pages", "frontmatter", inputs: ["**"]));

    [Fact]
    public async Task FrontMatterMovesToMetadata()
    {
        await Source.WriteTextFileAsync("/index.md", "---\ntitle: Home\ntags: [a, b]\n---\n# Hello");

        var result = await BuildSuccessfullyAsync(Pages());

        var file = Assert.Single(Outputs(result["pages"]));
        Assert.Equal("# Hello", await TextAsync(file));
        Assert.Equal("Home", file.Metadata.Own["title"]!.GetValue<string>());
        Assert.Equal(["a", "b"], file.Metadata.Own["tags"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("/", file.Metadata.Directory.FullName);
        Assert.Equal("text/markdown", file.EffectiveContentType);
    }

    [Fact]
    public async Task PageDateIsTheSourceDate()
    {
        await Source.WriteTextFileAsync("/a.html", "---\npage:\n  title: A\n---\nA");
        var modified = (await Source.GetFileMetadataAsync("/a.html")).LastWriteTimeUtc;

        var result = await BuildSuccessfullyAsync(Pages());

        // page.date is set before the front matter is merged: 'page' keeps both values.
        var page = Assert.IsType<NodeObject>(Assert.Single(Outputs(result["pages"])).Metadata.Own["page"]);
        Assert.Equal(modified, page["date"]!.GetValue<DateTime>());
        Assert.Equal("A", page["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnmatchedAndPlainFilesAreKept()
    {
        await Source.WriteTextFileAsync("/plain.html", "<p>no front matter</p>");
        await Source.WriteTextFileAsync("/style.css", "---\nnot: front matter\n---\n");

        var result = await BuildSuccessfullyAsync(Pages());

        var outputs = Outputs(result["pages"]);
        var plain = Assert.Single(outputs);
        Assert.Equal("/plain.html", plain.Path.FullName);

        // Without front matter the content is the source content, not a copy.
        Assert.Equal("<p>no front matter</p>", await TextAsync(plain));
        Assert.Equal(plain.Provenance.Inputs.Single().ContentHash, plain.ContentHash);

        // The stylesheet is not matched: it passes through, untouched.
        Assert.Equal(1, result["pages"].FilesPassedThrough);
    }
}
