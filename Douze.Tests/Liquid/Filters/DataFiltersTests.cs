using Douze.Nodes;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Liquid.Filters;

public class DataFiltersTests : FilterTests
{
    private static NodeObject Item(string title, string url, int year, string? locale = null)
    {
        var own = new NodeObject { ["title"] = NodeValue.Create(title), ["url"] = NodeValue.Create(url), ["year"] = NodeValue.Create(year) };

        if (locale != null)
        {
            own["locale"] = NodeValue.Create(locale);
        }

        return new NodeObject { ["data"] = new NodeValue<Metadata>(new Metadata(own, "/")) };
    }

    private static ScopeSet Pages() => Scopes(new NodeObject
    {
        ["pages"] = new NodeArray(
            Item("B", "/b/", 2021, "default"),
            Item("A", "/a/", 2020, "ja"),
            Item("C", "/c/", 2020, "default"),
            new NodeObject { ["data"] = new NodeValue<Metadata>(new Metadata(new NodeObject { ["url"] = NodeValue.Create("/untitled/") }, "/")) }),
    });

    // 40 routes (more than MemberIndex.MinimumCount): a module per range, one without a module, one with a number.
    private static ScopeSet Routes() => Scopes(new NodeObject
    {
        ["routes"] = new NodeArray([.. Enumerable.Range(0, 40).Select(i => i switch
        {
            35 => new NodeObject { ["n"] = NodeValue.Create(i) },
            36 => new NodeObject { ["module"] = NodeValue.Create(5), ["n"] = NodeValue.Create(i) },
            _ => new NodeObject { ["module"] = NodeValue.Create(i < 10 ? "a" : i < 30 ? "b" : "c"), ["n"] = NodeValue.Create(i) },
        })]),
        ["names"] = new NodeArray([.. Enumerable.Range(0, 40).Select(i => i == 20 ? null : NodeValue.Create($"s{i}"))]),
        ["few"] = new NodeArray([.. Enumerable.Range(0, 10).Select(i => i == 5 ? null : NodeValue.Create($"s{i}"))]),
    });

    [Theory]
    [InlineData("{{ routes | find_index: 'module', 'b' }}", "10")]
    [InlineData("{{ routes | find_index: 'module', 'c' }}", "30")]
    [InlineData("{{ routes | find_index: 'module', 'z' }}", "")]
    [InlineData("{{ routes | find_index: 'module', 5 }}", "36")]
    [InlineData("{{ routes | where: 'module', 'c' | map: 'n' | join: ',' }}", "30,31,32,33,34,37,38,39")]
    [InlineData("{{ routes | where: 'module', 'z' | size }}", "0")]
    [InlineData("{{ routes | where: 'module' | size }}", "39")]
    [InlineData("{{ routes | slice: 25, 10 | where: 'module', 'b' | map: 'n' | join: ',' }}", "25,26,27,28,29")]
    [InlineData("{{ routes | slice: 25, 10 | find_index: 'module', 'c' }}", "5")]
    [InlineData("{{ routes | slice: 25, 10 | slice: 3, 4 | find_index: 'module', 'c' }}", "2")]
    [InlineData("{{ routes | slice: 0, 10 | find_index: 'module', 'c' }}", "")]
    [InlineData("{{ routes | slice: -5, 3 | map: 'n' | join: ',' }}", "35,36,37")]
    [InlineData("{{ routes | slice: 38, 10 | map: 'n' | join: ',' }}", "38,39")]
    [InlineData("{{ routes | slice: 5, 0 | size }}", "0")]
    [InlineData("{{ routes | slice: 7 | map: 'n' | join: ',' }}", "7")]
    [InlineData("{{ names | find_index: 'x', 's5' }}", "5")]
    [InlineData("{{ names | find_index: 'x', 's25' }}", "")]
    [InlineData("{{ names | slice: 21, 10 | find_index: 'x', 's25' }}", "4")]
    [InlineData("{{ names | where: 'x', 's5' | size }}", "0")]
    [InlineData("{{ few | find_index: 'x', 's7' }}{{ few | find_index: 'x', 's3' }}", "3")]
    public async Task WhereAndFindIndexOverLargeArraysAreFluids(string template, string expected)
    {
        Assert.Equal(expected, await RenderAsync(template, scopes: Routes()));
    }

    [Fact]
    public async Task WhereExp()
    {
        Assert.Equal("B C ", await RenderAsync("{% assign list = pages | where_exp: 'item', \"item.data.locale == 'default'\" %}{% for p in list %}{{ p.data.title }} {% endfor %}", scopes: Pages()));
        Assert.Equal("A C ", await RenderAsync("{% assign list = pages | where_exp: 'p', 'p.data.year < 2021 and p.data.title' %}{% for p in list %}{{ p.data.title }} {% endfor %}", scopes: Pages()));
    }

    [Fact]
    public async Task WhereExpSeesTheTemplatesVariables()
    {
        Assert.Equal("B ", await RenderAsync("{% assign y = 2021 %}{% assign list = pages | where_exp: 'item', 'item.data.year == y' %}{% for p in list %}{{ p.data.title }} {% endfor %}", scopes: Pages()));
    }

    [Fact]
    public async Task GroupByExp()
    {
        var text = await RenderAsync("{% assign list = pages | group_by_exp: 'item', 'item.data.year' %}{% for g in list %}{{ g.name }}:{{ g.size }}:{% for p in g.items %}{{ p.data.url }}{% endfor %};{% endfor %}", scopes: Pages());
        Assert.Equal("2021:1:/b/;2020:2:/a//c/;:1:/untitled/;", text);
    }

    [Fact]
    public async Task SortByKeepsMissingValuesLast()
    {
        Assert.Equal("A B C /untitled/ ", await RenderAsync("{% assign list = pages | sort_by: 'data.title' %}{% for p in list %}{{ p.data.title | default: p.data.url }} {% endfor %}", scopes: Pages()));
        Assert.Equal("C B A /untitled/ ", await RenderAsync("{% assign list = pages | sort_by: 'data.title', 'desc' %}{% for p in list %}{{ p.data.title | default: p.data.url }} {% endfor %}", scopes: Pages()));

        // Numbers by value, and a stable sort.
        Assert.Equal("A C B /untitled/ ", await RenderAsync("{% assign list = pages | sort_by: 'data.year' %}{% for p in list %}{{ p.data.title | default: p.data.url }} {% endfor %}", scopes: Pages()));
    }

    [Fact]
    public async Task IndexBy()
    {
        Assert.Equal("C|", await RenderAsync("{% assign byUrl = pages | index_by: 'data.url' %}{{ byUrl['/c/'].data.title }}|{{ byUrl['/d/'].data.title }}", scopes: Pages()));
    }

    [Fact]
    public async Task Jsonify()
    {
        Assert.Equal("[1,2]", await RenderAsync("{{ list | jsonify }}", scopes: Scopes(new NodeObject { ["list"] = new NodeArray(NodeValue.Create(1), NodeValue.Create(2)) })));
    }
}
