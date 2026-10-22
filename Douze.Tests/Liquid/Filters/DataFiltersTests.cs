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
