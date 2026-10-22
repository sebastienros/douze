using Douze.Liquid;
using Douze.Nodes;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow;

public class ValueAccessTests
{
    private static NodeArray Pages(int count, Func<int, string>? title = null) =>
        new(Enumerable.Range(0, count).Select(i => (Node?)new NodeObject { ["title"] = NodeValue.Create(title?.Invoke(i) ?? $"Page {i}") }).ToArray());

    private static async Task<(string Rendered, Hash128 Hash, ValueAccess? Access)> RenderAsync(string template, NodeObject site)
    {
        var scopes = ScopeSet.Empty.WithSite(new SiteScope("step:collect", site));
        var deps = new DependencyTracker();
        var rendered = await Tests.Liquid.MetadataValueTests.RenderAsync(template, new Metadata(new NodeObject(), "/"), scopes, deps);
        var lookup = Assert.Single(deps.ToSet().Lookups);
        return (rendered, lookup.Hash, lookup.Access);
    }

    [Fact]
    public async Task ALoopDependsOnTheItemsItReadsAndTheirNumber()
    {
        const string Nav = "{% for p in pages limit: 2 %}{{ p.title }},{% endfor %}";

        var original = await RenderAsync(Nav, new NodeObject { ["pages"] = Pages(10) });
        var otherPageChanged = await RenderAsync(Nav, new NodeObject { ["pages"] = Pages(10, i => i == 5 ? "Edited" : $"Page {i}") });
        var listedPageChanged = await RenderAsync(Nav, new NodeObject { ["pages"] = Pages(10, i => i == 1 ? "Edited" : $"Page {i}") });
        var pageAdded = await RenderAsync(Nav, new NodeObject { ["pages"] = Pages(11) });

        Assert.Equal("Page 0,Page 1,", original.Rendered);
        Assert.False(original.Access!.IsWhole);
        Assert.Equal(original.Hash, otherPageChanged.Hash);
        Assert.NotEqual(original.Hash, listedPageChanged.Hash);
        Assert.NotEqual(original.Hash, pageAdded.Hash);
    }

    [Fact]
    public async Task EnumeratingDependsOnTheWholeValue()
    {
        const string Titles = "{{ pages | map: 'title' | join: ',' }}";

        var original = await RenderAsync(Titles, new NodeObject { ["pages"] = Pages(10) });
        var otherPageChanged = await RenderAsync(Titles, new NodeObject { ["pages"] = Pages(10, i => i == 5 ? "Edited" : $"Page {i}") });

        Assert.NotEqual(original.Hash, otherPageChanged.Hash);
    }

    [Fact]
    public async Task AMissingMemberIsADependency()
    {
        const string Template = "{{ site.subtitle }}";

        var original = await RenderAsync(Template, new NodeObject { ["site"] = new NodeObject { ["title"] = NodeValue.Create("Douze") } });
        var titleChanged = await RenderAsync(Template, new NodeObject { ["site"] = new NodeObject { ["title"] = NodeValue.Create("Other") } });
        var subtitleAdded = await RenderAsync(Template, new NodeObject { ["site"] = new NodeObject { ["title"] = NodeValue.Create("Douze"), ["subtitle"] = NodeValue.Create("!") } });

        Assert.Equal(original.Hash, titleChanged.Hash);
        Assert.NotEqual(original.Hash, subtitleAdded.Hash);
    }

    [Fact]
    public void ReadingTooManyPartsReadsTheWholeValue()
    {
        var recorder = new AccessRecorder();

        for (var i = 0; i < AccessRecorder.MaxEntries; i++)
        {
            recorder.Item(i).Member("title").ReadWhole();
        }

        Assert.False(recorder.Snapshot().IsWhole);

        recorder.Item(AccessRecorder.MaxEntries);
        Assert.True(recorder.Snapshot().IsWhole);

        // Too many parts in all, even when no value has too many.
        var deep = new AccessRecorder();

        for (var i = 0; i < 40; i++)
        {
            var item = deep.Item(i);

            for (var m = 0; m < 40; m++)
            {
                item.Member($"m{m}");
            }
        }

        Assert.True(deep.Snapshot().IsWhole);
    }
}
