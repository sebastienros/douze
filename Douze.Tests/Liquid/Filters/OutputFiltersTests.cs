using Douze.Nodes;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Liquid.Filters;

public class OutputFiltersTests : FilterTests
{
    private static ScopeSet SiteUrl(string url) => ScopeSet.FromSettings(new NodeObject { ["site"] = new NodeObject { ["url"] = NodeValue.Create(url) } });

    [Fact]
    public async Task XmlEscape()
    {
        Assert.Equal("Tom &amp; &quot;Jerry&quot; &lt;3 &#39;", await RenderAsync("{{ text | xml_escape }}", Page("/", ("text", NodeValue.Create("Tom & \"Jerry\" <3 '")))));
    }

    [Fact]
    public async Task AbsoluteUrl()
    {
        var scopes = SiteUrl("https://example.com/");

        Assert.Equal("https://example.com/a/", await RenderAsync("{{ '/a/' | absolute_url }}", scopes: scopes));
        Assert.Equal("https://example.com/a/", await RenderAsync("{{ 'a/' | absolute_url }}", scopes: scopes));
        Assert.Equal("https://other.org/x", await RenderAsync("{{ 'https://other.org/x' | absolute_url }}", scopes: scopes));
        Assert.Equal("https://example.com/blog/a/", await RenderAsync("{{ '/a/' | absolute_url }}", scopes: SiteUrl("https://example.com/blog")));

        // Without site.url, the url from the root.
        Assert.Equal("/a/", await RenderAsync("{{ 'a/' | absolute_url }}"));
    }

    [Fact]
    public async Task AbsoluteUrlDependsOnSiteUrl()
    {
        var deps = new DependencyTracker();
        await RenderAsync("{{ '/a/' | absolute_url }}", scopes: SiteUrl("https://example.com"), deps: deps);

        var lookup = Assert.Single(deps.ToSet().Lookups);
        Assert.Equal("site", lookup.Key);
        Assert.Equal(["url"], lookup.Access!.Members.Select(x => x.Key));
    }

    [Fact]
    public async Task RelativeUrl()
    {
        Assert.Equal("/blog/a/", await RenderAsync("{{ 'a/' | relative_url }}", scopes: SiteUrl("https://example.com/blog/")));
        Assert.Equal("/a/", await RenderAsync("{{ '/a/' | relative_url }}", scopes: SiteUrl("https://example.com")));
        Assert.Equal("/a/", await RenderAsync("{{ '/a/' | relative_url }}"));
    }

    [Fact]
    public async Task StripHtml()
    {
        var html = "<p>Hello <b>world</b></p><!-- note --><script>var a = '<p>';</script><style>p { }</style><br/>!";
        Assert.Equal("Hello world!", await RenderAsync("{{ html | strip_html }}", Page("/", ("html", NodeValue.Create(html)))));
    }

    [Fact]
    public async Task Dates()
    {
        var page = Page("/", ("date", NodeValue.Create(new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)))));

        Assert.Equal("2020-01-02T03:04:05+02:00", await RenderAsync("{{ date | date_to_xmlschema }}", page));
        Assert.Equal("Thu, 02 Jan 2020 03:04:05 +0200", await RenderAsync("{{ date | date_to_rfc822 }}", page));
        Assert.Equal("2020-01-02T00:00:00+00:00", await RenderAsync("{{ '2020-01-02T00:00:00Z' | date_to_xmlschema }}"));
    }
}
