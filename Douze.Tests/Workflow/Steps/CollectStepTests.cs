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
            Source.SetLastWriteTimeUtc("/a.html", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

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

    private static WorkflowDefinition CollectWithUrls(NodeObject? with = null) => Workflow(
        Step("pages", "frontmatter", inputs: ["**"]),
        Step("permalink", "permalink", needs: ["pages"]),
        Step("collections", "collect", needs: ["permalink"], with: with));

    [Fact]
    public async Task UrlsMapNormalizedUrlsToTheItemsOfAll()
    {
        await Source.WriteTextFileAsync("/index.md", "---\ndate: 2020-01-01\n---\nhome");
        await Source.WriteTextFileAsync("/docs/a.md", "---\ndate: 2020-01-02\n---\na");
        await Source.WriteTextFileAsync("/feed.html", "---\ndate: 2020-01-03\npermalink: feed.xml\n---\nfeed");
        await Source.WriteTextFileAsync("/b.html", "---\ndate: 2020-01-04\npermalink: /docs/a/\n---\nsame url");

        var result = await BuildSuccessfullyAsync(CollectWithUrls());

        var collections = Contribution(result).Values["collections"]!.AsObject();
        var urls = collections["urls"]!.AsObject();

        // feed.xml is not a page; b.html has the url of a.md, which comes first in 'all'.
        Assert.Equal(["/", "/docs/a/"], urls.Select(x => x.Key));

        var all = collections["all"]!.AsArray();
        Assert.Same(all[1], urls["/docs/a/"]);
        Assert.Same(all[0], urls["/"]);
    }

    [Fact]
    public async Task ItemsExposeDateUrlAndInputPathForStandardFilters()
    {
        // Dates whose text does not sort like the dates; c.md has no date and comes first (page.date of 2019).
        await Source.WriteTextFileAsync("/posts/b.md", "---\ntags: post\ntitle: B\ndate: 2021-12-01\n---\nb");
        await Source.WriteTextFileAsync("/posts/a.md", "---\ntags: post\ntitle: A\ndate: 2022-01-15\n---\na");
        await Source.WriteTextFileAsync("/posts/c.md", "---\ntags: post\ntitle: C\n---\nc");
        Source.SetLastWriteTimeUtc("/posts/c.md", new DateTime(2019, 6, 30, 0, 0, 0, DateTimeKind.Utc));
        await Source.WriteTextFileAsync("/list.html",
            "{% assign items = collections.post | sort: 'url' %}{% for p in items %}{{ p.data.title }}:{{ p.url }}:{{ p.inputPath }}:{{ p.date | date: '%Y' }};{% endfor %}"
            + "|{% assign items = collections.post | sort: 'date' | reverse %}{% for p in items %}{{ p.data.title }}{% endfor %}"
            + "|{% assign found = collections.post | where: 'url', '/posts/b/index.html' | first %}{{ found.data.title }}");

        var result = await BuildSuccessfullyAsync(Workflow(
            Step("pages", "frontmatter", inputs: ["**"]),
            Step("permalink", "permalink", needs: ["pages"]),
            Step("collections", "collect", needs: ["permalink"]),
            Step("render", "liquid", needs: ["permalink", "collections"])));

        var list = result["render"].Results.Single(x => x.Inputs.Single().Path == "/list/index.html").Files.Single();
        Assert.Equal("A:/posts/a/index.html:/posts/a.md:2022;B:/posts/b/index.html:/posts/b.md:2021;C:/posts/c/index.html:/posts/c.md:2019;|ABC|B", await TextAsync(list));
    }

    [Fact]
    public async Task ReadingOneUrlRecordsThatMemberOnly()
    {
        await Source.WriteTextFileAsync("/docs/a.md", "---\ntitle: A\n---\na");
        await Source.WriteTextFileAsync("/docs/b.md", "---\ntitle: B\n---\n{{ collections.urls['/docs/a/'].data.title }}");

        var result = await BuildSuccessfullyAsync(Workflow(
            Step("pages", "frontmatter", inputs: ["**"]),
            Step("permalink", "permalink", needs: ["pages"]),
            Step("collections", "collect", needs: ["permalink"]),
            Step("render", "liquid", needs: ["permalink", "collections"])));

        var unit = result["render"].Results.Single(x => x.Inputs.Single().Path == "/docs/b/index.md");
        Assert.Equal("A", await TextAsync(unit.Files.Single()));

        var lookup = Assert.Single(unit.Dependencies.Lookups, x => x.Key == "collections");
        var urls = Assert.Single(lookup.Access!.Members);
        Assert.Equal("urls", urls.Key);
        Assert.Equal(["/docs/a/"], urls.Access.Members.Select(x => x.Key));
    }

    [Fact]
    public async Task ContentIsExposedOnRequest()
    {
        await Source.WriteTextFileAsync("/a.md", "---\ntitle: A\n---\nbody of a");

        var without = await BuildSuccessfullyAsync(CollectWithUrls());
        Assert.False(without["collections"].Node.Step.ReadsContent);
        Assert.Null(Contribution(without).Values["collections"]!["all"]![0]!["content"]);

        Site = null;
        var with = await BuildSuccessfullyAsync(CollectWithUrls(With(("content", NodeValue.Create(true)))));
        Assert.True(with["collections"].Node.Step.ReadsContent);
        Assert.Equal("body of a", Contribution(with).Values["collections"]!["all"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task NameIsTheScopeKey()
    {
        await Source.WriteTextFileAsync("/a.md", "a");

        var result = await BuildSuccessfullyAsync(Workflow(
            Step("pages", "frontmatter", inputs: ["**"]),
            Step("permalink", "permalink", needs: ["pages"]),
            Step("collections", "collect", needs: ["permalink"]),
            Step("docs", "collect", needs: ["permalink"], with: With(("name", NodeValue.Create("docs"))))));

        Assert.Equal(["docs", "collections"], result.Scopes.Site.Select(x => x.Values.Single().Key).OrderBy(x => x == "collections"));
        Assert.Equal(["/a/"], result["docs"].Contributions.Site.Single().Values["docs"]!["urls"]!.AsObject().Select(x => x.Key));
    }
}
