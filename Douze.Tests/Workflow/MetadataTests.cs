using Douze.Nodes;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow;

public class MetadataTests
{
    [Fact]
    public void WithSharesNestedNodes()
    {
        var page = new NodeObject { ["title"] = NodeValue.Create("Home") };
        var original = new Metadata(new NodeObject { ["page"] = page, ["layout"] = NodeValue.Create("a.html") }, "/blog/");

        var derived = original.With(own => own["layout"] = NodeValue.Create("b.html"));

        Assert.NotSame(original.Own, derived.Own);
        Assert.Same(page, derived.Own["page"]);
        Assert.Equal("a.html", original.Own["layout"]!.GetValue<string>());
        Assert.Equal("b.html", derived.Own["layout"]!.GetValue<string>());
        Assert.Equal(original.Directory, derived.Directory);
        Assert.NotEqual(original.Hash, derived.Hash);
    }

    [Fact]
    public void HashIgnoresKeyInsertionOrder()
    {
        var a = new Metadata(new NodeObject
        {
            ["title"] = NodeValue.Create("Home"),
            ["page"] = new NodeObject { ["x"] = NodeValue.Create(1), ["y"] = NodeValue.Create(true) },
        }, "/");

        var b = new Metadata(new NodeObject
        {
            ["page"] = new NodeObject { ["y"] = NodeValue.Create(true), ["x"] = NodeValue.Create(1) },
            ["title"] = NodeValue.Create("Home"),
        }, "/");

        Assert.Equal(a.Hash, b.Hash);
    }

    [Fact]
    public void DirectoryIsPartOfTheHash()
    {
        var own = new NodeObject { ["title"] = NodeValue.Create("Home") };

        var root = new Metadata(own, "/");
        var blog = root.WithDirectory("/blog/");

        Assert.NotEqual(root.Hash, blog.Hash);
        Assert.Equal(root.Hash, blog.WithDirectory("/").Hash);
    }
}
