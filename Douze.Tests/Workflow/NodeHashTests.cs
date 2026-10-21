using Douze.Nodes;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow;

public class NodeHashTests
{
    [Fact]
    public void EqualTreesHaveEqualHashesWhateverTheKeyOrder()
    {
        var a = new NodeObject { ["x"] = NodeValue.Create(1), ["y"] = new NodeArray(NodeValue.Create("a"), null) };
        var b = new NodeObject { ["y"] = new NodeArray(NodeValue.Create("a"), null), ["x"] = NodeValue.Create(1) };

        Assert.Equal(NodeHash.Of(a), NodeHash.Of(b));
        Assert.NotEqual(NodeHash.Of(a), NodeHash.Of(new NodeObject { ["x"] = NodeValue.Create(2), ["y"] = new NodeArray(NodeValue.Create("a"), null) }));
    }

    [Fact]
    public void ValuesAreHashedWithTheirType()
    {
        Node?[] values =
        [
            NodeValue.Create(1), NodeValue.Create(1L), NodeValue.Create(1m), NodeValue.Create(1.0), NodeValue.Create("1"), NodeValue.Create(true),
            NodeValue.Create(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)), NodeValue.Create(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Local)),
            NodeValue.Create(1.00m), null, new NodeArray(), new NodeObject(),
        ];

        Assert.Equal(values.Length, values.Select(NodeHash.Of).Distinct().Count());
    }

    [Fact]
    public void SharedSubtreesAreHashedOnce()
    {
        // The pages of a pagination are shared by every page: their hash is cached on the shared node.
        var pages = new NodeArray(Enumerable.Range(0, 100).Select(i => (Node?)new NodeObject { ["pageNumber"] = NodeValue.Create(i) }).ToArray());
        var first = new Metadata(new NodeObject { ["pagination"] = new NodeObject { ["pages"] = pages, ["pageNumber"] = NodeValue.Create(0) } }, "/");
        var second = new Metadata(new NodeObject { ["pagination"] = new NodeObject { ["pages"] = pages, ["pageNumber"] = NodeValue.Create(1) } }, "/");

        Assert.False(pages.HashCache.TryGet(out _, out _));
        _ = first.Hash;
        Assert.True(pages.HashCache.TryGet(out var cached, out _));
        Assert.Equal(cached, NodeHash.Of(pages));
        Assert.NotEqual(first.Hash, second.Hash);
    }

    [Fact]
    public void ChangingANodeResetsItsHash()
    {
        var items = new NodeArray(NodeValue.Create("a"));
        var obj = new NodeObject { ["a"] = NodeValue.Create(1) };
        var before = (NodeHash.Of(items), NodeHash.Of(obj));

        items.Add(NodeValue.Create("b"));
        obj["a"] = NodeValue.Create(2);

        Assert.NotEqual(before.Item1, NodeHash.Of(items));
        Assert.NotEqual(before.Item2, NodeHash.Of(obj));
        Assert.Equal(NodeHash.Of(new NodeArray(NodeValue.Create("a"), NodeValue.Create("b"))), NodeHash.Of(items));
    }

    [Fact]
    public void MetadataInANodeIsHashedByItsHash()
    {
        var a = new Metadata(new NodeObject { ["title"] = NodeValue.Create("A") }, "/");
        var b = new Metadata(new NodeObject { ["title"] = NodeValue.Create("B") }, "/");

        Assert.NotEqual(NodeHash.Of(new NodeValue<Metadata>(a)), NodeHash.Of(new NodeValue<Metadata>(b)));
        Assert.Equal(NodeHash.Of(new NodeValue<Metadata>(a)), NodeHash.Of(new NodeValue<Metadata>(new Metadata(new NodeObject { ["title"] = NodeValue.Create("A") }, "/"))));
    }
}
