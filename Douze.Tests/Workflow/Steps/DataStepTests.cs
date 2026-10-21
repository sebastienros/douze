using Douze.FileSystems;
using Douze.Nodes;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow.Steps;

public class DataStepTests : StepTestBase
{
    private static WorkflowDefinition SiteData() => Workflow(Step("data", "data", inputs: ["_data/**"], with: With(("scope", NodeValue.Create("site")))));

    private static Node? SiteValue(BuildResult result, string key) => result.Resolve(Metadata.Empty, key);

    [Fact]
    public async Task KeyedByFileName()
    {
        await Source.WriteTextFileAsync("/_data/colors.json", """["red", "blue"]""");
        await Source.WriteTextFileAsync("/_data/site.yml", "title: Douze\nurl: https://douze.dev");
        await Source.WriteTextFileAsync("/_data/menu.xml", "<menu><item>Home</item></menu>");

        var result = await BuildSuccessfullyAsync(SiteData());

        Assert.Equal(["red", "blue"], SiteValue(result, "colors")!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("Douze", SiteValue(result, "site")!["title"]!.GetValue<string>());
        Assert.Equal("menu", SiteValue(result, "menu")!["menu"]!["name"]!.GetValue<string>());

        // Keys are not merged into the site: 'title' is under 'site'.
        Assert.Null(SiteValue(result, "title"));
        Assert.Equal("/_data/site.yml", result.Scopes.Site.Single().KeySources!["site"]);
        Assert.Empty(result["data"].Results.Single().Outputs);
    }

    [Fact]
    public async Task NestedFolderKey()
    {
        await Source.WriteTextFileAsync("/_data/pkgs/Aspire.Hosting.json", """{ "name": "Aspire.Hosting" }""");
        await Source.WriteTextFileAsync("/_data/pkgs/Aspire.Redis.json", """{ "name": "Aspire.Redis" }""");

        var result = await BuildSuccessfullyAsync(SiteData());

        var pkgs = Assert.IsType<NodeObject>(SiteValue(result, "pkgs"));
        Assert.Equal(["Aspire.Hosting", "Aspire.Redis"], pkgs.Select(x => x.Key));
        Assert.Equal("Aspire.Hosting", pkgs["Aspire.Hosting"]!["name"]!.GetValue<string>());

        // A key built from a folder is recorded as the folder.
        Assert.Equal("/_data/pkgs/", result.Scopes.Site.Single().KeySources!["pkgs"]);
    }

    [Fact]
    public async Task ScalarFile()
    {
        await Source.WriteTextFileAsync("/_data/layout.yml", "layout.html");
        await Source.WriteTextFileAsync("/_data/count.json", "42");

        var result = await BuildSuccessfullyAsync(SiteData());

        Assert.Equal("layout.html", SiteValue(result, "layout")!.GetValue<string>());
        Assert.Equal(42m, SiteValue(result, "count")!.GetValue<decimal>());
    }

    [Fact]
    public async Task DuplicateKeyError()
    {
        await Source.WriteTextFileAsync("/_data/colors.json", "[]");
        await Source.WriteTextFileAsync("/_data/colors.yml", "- red");

        var result = await BuildAsync(SiteData());

        var error = Assert.Single(result.Errors);
        Assert.Equal("data key 'colors' is defined by both '/_data/colors.json' and '/_data/colors.yml'", error.Message);
        Assert.Equal("error: data: data key 'colors' is defined by both '/_data/colors.json' and '/_data/colors.yml'", error.ToString());
    }

    [Fact]
    public async Task FolderMergeOrder()
    {
        await Source.WriteTextFileAsync("/blog/a.yml", "layout: a.html\ntitle: A");
        await Source.WriteTextFileAsync("/blog/b.yml", "layout: b.html");
        await Source.WriteTextFileAsync("/blog/2020/c.json", """{ "title": "C" }""");

        var result = await BuildSuccessfullyAsync(Workflow(Step("data", "data", inputs: ["**"], with: With(("scope", NodeValue.Create("folder"))))));

        // Files of a folder are merged in path order: b.yml overrides a.yml.
        var blog = result.Scopes.Folders["/blog/"];
        Assert.Equal("b.html", blog.Values["layout"]!.GetValue<string>());
        Assert.Equal("A", blog.Values["title"]!.GetValue<string>());
        Assert.Equal(["/blog/a.yml", "/blog/b.yml"], blog.Sources.Select(x => x.FullName));

        // The nearest folder wins.
        var page = new Metadata(new NodeObject(), "/blog/2020/");
        Assert.Equal("C", result.Resolve(page, "title")!.GetValue<string>());
        Assert.Equal("b.html", result.Resolve(page, "layout")!.GetValue<string>());
    }
}
