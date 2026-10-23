using Douze.Liquid.Filters;
using Douze.Nodes;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Liquid.Filters;

public class CollectionFiltersTests : FilterTests
{
    private static NodeObject Item(string url, string title)
    {
        var own = new NodeObject { ["url"] = NodeValue.Create(url), ["title"] = NodeValue.Create(title) };
        return new NodeObject { ["data"] = new NodeValue<Metadata>(new Metadata(own, "/")) };
    }

    private static NodeArray Posts() => new([Item("/a/index.html", "A"), Item("/b/index.html", "B"), Item("/c/index.html", "C")]);

    private static ScopeSet Data(NodeArray posts) => Scopes(new NodeObject { ["collections"] = new NodeObject { ["posts"] = posts } });

    private const string Template = "{% assign p = collections.posts | previous_item %}{% assign n = collections.posts | next_item %}{{ p.data.title }}|{{ n.data.title }}";

    [Theory]
    [InlineData("/a/index.html", "|B")]
    [InlineData("/b/index.html", "A|C")]
    [InlineData("/c/", "B|")]
    [InlineData("/missing/", "|")]
    public async Task NeighborsOfThePageUrl(string url, string expected)
    {
        var page = Page("/", ("url", NodeValue.Create(url)));
        Assert.Equal(expected, await RenderAsync(Template, page, Data(Posts())));
    }

    [Fact]
    public async Task UrlArgument()
    {
        Assert.Equal("A|C", await RenderAsync("{% assign p = collections.posts | previous_item: '/b/' %}{% assign n = collections.posts | next_item: '/b/index.html' %}{{ p.data.title }}|{{ n.data.title }}", scopes: Data(Posts())));
    }

    [Fact]
    public async Task OtherArraysAreSearched()
    {
        var page = Page("/", ("url", NodeValue.Create("/b/")));
        Assert.Equal("C|A", await RenderAsync("{% assign r = collections.posts | reverse %}{% assign p = r | previous_item %}{% assign n = r | next_item %}{{ p.data.title }}|{{ n.data.title }}", page, Data(Posts())));
    }

    [Fact]
    public async Task DependsOnThePageAndItsNeighborsOnly()
    {
        var posts = new NodeArray(Enumerable.Range(0, 10).Select(i => (Node?)Item($"/p{i}/", $"P{i}")).ToArray());
        var deps = new DependencyTracker();
        var page = Page("/", ("url", NodeValue.Create("/p5/")));

        Assert.Equal("P4|P6", await RenderAsync(Template, page, Data(posts), deps: deps));

        var lookup = Assert.Single(deps.ToSet().Lookups, x => x.Key == "collections");
        var read = Assert.Single(lookup.Access!.Members);
        Assert.Equal("posts", read.Key);
        Assert.Equal([4, 5, 6], read.Access.Items.Select(x => x.Index).Order());
    }

    [Fact]
    public async Task APageNotInTheCollectionDependsOnAllOfIt()
    {
        var deps = new DependencyTracker();
        await RenderAsync(Template, Page("/", ("url", NodeValue.Create("/x/"))), Data(Posts()), deps: deps);

        var lookup = Assert.Single(deps.ToSet().Lookups, x => x.Key == "collections");
        Assert.True(Assert.Single(lookup.Access!.Members).Access.IsWhole);
    }

    [Fact]
    public async Task TheLastPageDependsOnTheItemAfterIt()
    {
        var deps = new DependencyTracker();
        await RenderAsync(Template, Page("/", ("url", NodeValue.Create("/c/"))), Data(Posts()), deps: deps);

        var lookup = Assert.Single(deps.ToSet().Lookups, x => x.Key == "collections");
        Assert.Contains(3, Assert.Single(lookup.Access!.Members).Access.Items.Select(x => x.Index));
    }

    [Fact]
    public async Task IndexIsBuiltOncePerCollection()
    {
        var posts = Posts();
        var scopes = Data(posts);
        Assert.Null(CollectionFilters.IndexOf(posts));

        await RenderAsync("{{ collections.posts | next_item: '/a/' }}", scopes: scopes);
        var index = CollectionFilters.IndexOf(posts);
        Assert.NotNull(index);

        await RenderAsync("{{ collections.posts | previous_item: '/c/' }}", scopes: scopes);
        Assert.Same(index, CollectionFilters.IndexOf(posts));
    }
}
