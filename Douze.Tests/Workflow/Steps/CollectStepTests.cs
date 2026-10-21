using Douze.FileSystems;
using Douze.Nodes;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow.Steps;

public class CollectStepTests : StepTestBase
{
    private static WorkflowDefinition Collect() => Workflow(
        Step("pages", "frontmatter", inputs: ["**"]),
        Step("collections", "collect", needs: ["pages"]));

    private static SiteScope Contribution(BuildResult result) => result["collections"].Contributions.Site.Single();

    [Fact]
    public async Task NoFilesEmitted()
    {
        await Source.WriteTextFileAsync("/a.html", "---\ntags: post\n---\na");
        await Source.WriteTextFileAsync("/style.css", "body {}");

        var result = await BuildSuccessfullyAsync(Collect());

        var run = result["collections"];
        Assert.Empty(run.Results.Single().Outputs);
        Assert.Equal(1, run.FilesConsumed);
        Assert.Equal(1, run.FilesPassedThrough);
        Assert.Equal("step:collections", Contribution(result).Source);
    }

    [Fact]
    public async Task ContributionHashChangesWithMetadataOnly()
    {
        async Task<Hash128> HashAsync(string content)
        {
            Source = new();
            Site = null;
            await Source.WriteTextFileAsync("/a.html", content);

            // page.date is part of the metadata: the same date in every build.
            (await Source.GetFileMetadataAsync("/a.html")).LastWriteTimeUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            return Contribution(await BuildSuccessfullyAsync(Collect())).Hash;
        }

        var original = await HashAsync("---\ntitle: A\n---\nfirst body");
        var bodyChanged = await HashAsync("---\ntitle: A\n---\nsecond body");
        var titleChanged = await HashAsync("---\ntitle: B\n---\nfirst body");

        Assert.Equal(original, bodyChanged);
        Assert.NotEqual(original, titleChanged);
    }

    [Fact]
    public async Task ItemsWrapMetadata()
    {
        await Source.WriteTextFileAsync("/posts/posts.yml", "tags: post");
        await Source.WriteTextFileAsync("/posts/b.html", "---\ntitle: B\ndate: 2020-01-02\n---\nb");
        await Source.WriteTextFileAsync("/posts/a.html", "---\ntitle: A\ndate: 2020-01-01\ntags: [post, post]\n---\na");

        var result = await BuildSuccessfullyAsync(Workflow(
            Step("data", "data", inputs: ["**/*.yml"], with: With(("scope", NodeValue.Create("folder")))),
            Step("pages", "frontmatter", inputs: ["**/*.html"], needs: ["data"]),
            Step("collections", "collect", needs: ["pages"])));

        // The tags of b.html come from the folder data; a.html lists its tag twice and is collected once.
        var posts = result.Collection("post");
        Assert.Equal(["A", "B"], posts.Select(x => x.Own["title"]!.GetValue<string>()));

        var item = Contribution(result).Values["collections"]!["post"]![0]!;
        var data = Assert.IsType<NodeValue<Metadata>>(item["data"]);
        Assert.Same(Outputs(result["pages"]).Single(x => x.Path == "/posts/a.html").Metadata, data.Value);
    }
}
