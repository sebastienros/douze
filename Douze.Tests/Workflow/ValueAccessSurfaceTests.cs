using Douze.Nodes;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow;

/// <summary>
/// Every way a template can read a value from the scopes records what it read (<see cref="ValueAccess"/>): the
/// projected hash of the lookup changes when a part the template read changes, and only then. Filters, loops,
/// indexing, comparisons and conversions are the surface Fluid exposes; a path that read a value without recording it
/// would replay a stale result.
/// </summary>
public class ValueAccessSurfaceTests
{
    private static NodeArray Pages(int count, int edited = -1) =>
        new(Enumerable.Range(0, count).Select(i => (Node?)new NodeObject { ["title"] = NodeValue.Create(i == edited ? "Edited" : $"Page {i}"), ["n"] = NodeValue.Create(i) }).ToArray());

    private static NodeObject Members(int count, int edited = -1)
    {
        var site = new NodeObject();

        for (var i = 0; i < count; i++)
        {
            site[$"m{i}"] = NodeValue.Create(i == edited ? "Edited" : $"Value {i}");
        }

        return site;
    }

    private static async Task<Dictionary<string, Hash128>> HashesAsync(string template, params (string Key, Node Value)[] values)
    {
        var site = new NodeObject();

        foreach (var (key, value) in values)
        {
            site[key] = value;
        }

        var scopes = ScopeSet.Empty.WithSite(new SiteScope("step:collect", site));
        var deps = new DependencyTracker();
        await Tests.Liquid.MetadataValueTests.RenderAsync(template, new Metadata(new NodeObject(), "/"), scopes, deps);

        return deps.ToSet().Lookups.ToDictionary(x => x.Key, x => x.Hash, StringComparer.Ordinal);
    }

    private static async Task<Hash128> HashAsync(string template, string key, Node value) => (await HashesAsync(template, (key, value)))[key];

    /// <summary>
    /// What a template depends on: the whole value, the first item or member, the fifth, the number of items or
    /// members, or a combination. Fluid reads the count of an array for every loop, <c>first</c> and <c>slice</c>.
    /// </summary>
    private static (bool First, bool Fifth, bool Count) Expect(string expected) => expected switch
    {
        "whole" => (true, true, true),
        "first" => (true, false, false),
        "first+count" => (true, false, true),
        "fifth" => (false, true, false),
        "fifth+count" => (false, true, true),
        "count" => (false, false, true),
        _ => throw new ArgumentException(expected),
    };

    public static TheoryData<string, string> ArrayTemplates => new()
    {
        { "{{ pages }}", "whole" },
        { "{{ pages | json }}", "whole" },
        { "{{ pages | map: 'title' | join: ',' }}", "whole" },
        { "{{ pages | map: 'title' | first }}", "whole" },
        { "{{ pages | map: 'n' | sum }}", "whole" },
        { "{{ pages | reverse | map: 'title' | join: ',' }}", "whole" },
        { "{{ pages | sort: 'title' | map: 'title' | join: ',' }}", "whole" },
        { "{{ pages | where: 'title', 'Page 5' | size }}", "whole" },
        { "{{ pages | reject: 'title', 'Page 5' | size }}", "whole" },
        { "{{ pages | find_index: 'title', 'Page 5' }}", "whole" },
        { "{{ pages | has: 'title', 'Page 5' }}", "whole" },
        { "{{ pages | uniq | size }}", "whole" },
        { "{{ pages | compact | size }}", "whole" },
        { "{{ pages | concat: pages | size }}", "whole" },
        { "{{ pages | default: 'none' }}", "whole" },
        { "{% for p in pages %}{{ p.title }}{% endfor %}", "whole" },
        { "{% if pages contains pages[0] %}yes{% endif %}", "whole" },
        { "{% if pages == empty %}yes{% endif %}", "whole" },
        { "{% for p in pages limit: 2 %}{{ p.title }}{% endfor %}", "first+count" },
        { "{% tablerow p in pages cols: 2 limit: 2 %}{{ p.title }}{% endtablerow %}", "first+count" },
        { "{% assign f = pages | first %}{{ f.title }}", "first+count" },
        { "{{ pages | slice: 0, 2 | map: 'title' | join: ',' }}", "first+count" },
        { "{{ pages[0].title }}", "first" },
        { "{{ pages.first.title }}", "first" },
        { "{% for p in pages offset: 5 limit: 1 %}{{ p.title }}{% endfor %}", "fifth+count" },
        { "{{ pages[-5].title }}", "fifth+count" },
        { "{{ pages[5].title }}", "fifth" },
        { "{{ pages | size }}", "count" },
        { "{{ pages.size }}", "count" },
        { "{% assign l = pages | last %}{{ l.n }}", "count" },
        { "{{ pages.last.n }}", "count" },
        { "{{ pages[-1].n }}", "count" },
        { "{% for p in pages reversed limit: 1 %}{{ p.n }}{% endfor %}", "count" },
        { "{% if empty == pages %}yes{% endif %}", "count" },
        { "{% if pages.size > 3 %}yes{% endif %}", "count" },
    };

    [Theory]
    [MemberData(nameof(ArrayTemplates))]
    public async Task ArrayReadsAreRecorded(string template, string expected)
    {
        var original = await HashAsync(template, "pages", Pages(10));
        var firstEdited = await HashAsync(template, "pages", Pages(10, edited: 0));
        var fifthEdited = await HashAsync(template, "pages", Pages(10, edited: 5));
        var itemAdded = await HashAsync(template, "pages", Pages(11));

        var (first, fifth, count) = Expect(expected);

        Assert.True(first == (original != firstEdited), $"first item edited: expected {(first ? "a change" : "no change")}");
        Assert.True(fifth == (original != fifthEdited), $"fifth item edited: expected {(fifth ? "a change" : "no change")}");
        Assert.True(count == (original != itemAdded), $"item added: expected {(count ? "a change" : "no change")}");
    }

    public static TheoryData<string, string> ObjectTemplates => new()
    {
        { "{{ site | json }}", "whole" },
        { "{% for kv in site %}{{ kv[0] }}={{ kv[1] }}{% endfor %}", "whole" },
        { "{% if site contains 'm0' %}yes{% endif %}", "whole" },
        { "{% if site == empty %}yes{% endif %}", "whole" },
        { "{{ site.m0 }}", "first" },
        { "{{ site['m0'] }}", "first" },
        { "{{ site.m0 | upcase }}", "first" },
        { "{{ site | map: 'm0' }}", "first" },
        { "{{ site.size }}", "count" },
        { "{{ site | size }}", "count" },
        { "{% if empty == site %}yes{% endif %}", "count" },
        { "{{ site | default: 'none' }}", "count" },
    };

    [Theory]
    [MemberData(nameof(ObjectTemplates))]
    public async Task ObjectReadsAreRecorded(string template, string expected)
    {
        var original = await HashAsync(template, "site", Members(10));
        var firstEdited = await HashAsync(template, "site", Members(10, edited: 0));
        var fifthEdited = await HashAsync(template, "site", Members(10, edited: 5));
        var memberAdded = await HashAsync(template, "site", Members(11));

        var (first, fifth, count) = Expect(expected);

        Assert.True(first == (original != firstEdited), $"first member edited: expected {(first ? "a change" : "no change")}");
        Assert.True(fifth == (original != fifthEdited), $"fifth member edited: expected {(fifth ? "a change" : "no change")}");
        Assert.True(count == (original != memberAdded), $"member added: expected {(count ? "a change" : "no change")}");
    }

    [Fact]
    public async Task ComparingTwoValuesReadsBoth()
    {
        // The value on the right of == or contains is read by the comparison of the one on the left.
        foreach (var template in new[] { "{% if pages == other %}yes{% endif %}", "{% if other contains pages[0] %}yes{% endif %}" })
        {
            var original = await HashesAsync(template, ("pages", Pages(10)), ("other", Pages(10)));
            var otherEdited = await HashesAsync(template, ("pages", Pages(10)), ("other", Pages(10, edited: 5)));
            var firstEdited = await HashesAsync(template, ("pages", Pages(10, edited: 0)), ("other", Pages(10)));

            Assert.True(original["other"] != otherEdited["other"], $"{template}: 'other' edited");
            Assert.True(original["pages"] != firstEdited["pages"], $"{template}: first page edited");
        }

        var objects = "{% if site == other %}yes{% endif %}";
        var before = await HashesAsync(objects, ("site", Members(3)), ("other", Members(3)));
        var after = await HashesAsync(objects, ("site", Members(3)), ("other", Members(3, edited: 1)));
        Assert.NotEqual(before["other"], after["other"]);
    }
}
