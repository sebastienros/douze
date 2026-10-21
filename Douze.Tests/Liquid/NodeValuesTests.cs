using Douze.FileSystems;
using Douze.Nodes;
using Douze.Workflow;
using Fluid;
using Xunit;

namespace Douze.Tests.Liquid;

public class NodeValuesTests
{
    private static Task<string> RenderAsync(string template, NodeObject own) => MetadataValueTests.RenderAsync(template, new Metadata(own, "/"), ScopeSet.Empty);

    [Theory]
    [InlineData(2020, 1, 1, 23, 59, 30, 0, "2020/01/01 23:59:30|Wednesday, 01 January 2020 23:59:30|2020/01/01")]
    [InlineData(2019, 12, 31, 0, 0, 1, 0, "2019/12/31 00:00:01|Tuesday, 31 December 2019 00:00:01|2019/12/31")]
    [InlineData(2024, 7, 14, 12, 0, 0, 123, "2024/07/14 12:00:00|Sunday, 14 July 2024 12:00:00|2024/07/14")]
    public async Task DateTimeRendersLikeTheProcessorPipeline(int year, int month, int day, int hour, int minute, int second, int millisecond, string expected)
    {
        // page.date reaches templates as a NodeValue<DateTime> holding the file's UTC modification time. The expected
        // values are what the processor pipeline (removed in phase 1b) rendered for the formats the Sample uses
        // (open question 4 of the workflow engine plan).
        var date = new DateTime(year, month, day, hour, minute, second, millisecond, DateTimeKind.Utc);
        var own = new NodeObject { ["d"] = NodeValue.Create(date) };

        var rendered = await RenderAsync("{{ d | date: '%Y/%m/%d %H:%M:%S' }}|{{ d | date: '%c' }}|{{ d | date: '%Y/%m/%d' }}", own);

        Assert.Equal(expected, rendered);
    }

    [Fact]
    public async Task ArraysAndObjectsEnumerate()
    {
        var own = new NodeObject
        {
            ["items"] = new NodeArray(NodeValue.Create("a"), NodeValue.Create(2), NodeValue.Create(true)),
            ["map"] = new NodeObject { ["x"] = NodeValue.Create(1), ["y"] = NodeValue.Create("two") },
            ["people"] = new NodeArray(new NodeObject { ["name"] = NodeValue.Create("Luke") }, new NodeObject { ["name"] = NodeValue.Create("Leia") }),
        };

        Assert.Equal("a,2,true,", await RenderAsync("{% for i in items %}{{ i }},{% endfor %}", own));
        Assert.Equal("x=1;y=two;", await RenderAsync("{% for p in map %}{{ p[0] }}={{ p[1] }};{% endfor %}", own));
        Assert.Equal("Luke Leia", await RenderAsync("{{ people | map: 'name' | join: ' ' }}", own));
        Assert.Equal("a true two", await RenderAsync("{{ items.first }} {{ items.last }} {{ map.y }}", own));
        Assert.Equal("2 a", await RenderAsync("{{ items[1] }} {{ items[-3] }}", own));
    }

    [Fact]
    public async Task Size()
    {
        var own = new NodeObject
        {
            ["items"] = new NodeArray(NodeValue.Create("a"), NodeValue.Create("b")),
            ["map"] = new NodeObject { ["x"] = NodeValue.Create(1), ["y"] = NodeValue.Create(2), ["z"] = NodeValue.Create(3) },
            ["none"] = new NodeArray(),
        };

        Assert.Equal("2 3 0", await RenderAsync("{{ items.size }} {{ map | size }} {{ none | size }}", own));
    }

    [Fact]
    public async Task ForWithLimitConvertsOnlyTheItemsItRenders()
    {
        // Fluid reads arrays implementing IFluidArray by index: a nav loop over 'collections.all limit: 3' must not
        // convert every item of the collection (benchmark finding 1).
        var conversions = new int[1];
        var items = new NodeArray(Enumerable.Range(0, 1000).Select(i => (Node?)new CountingNode(i, conversions)).ToArray());
        var own = new NodeObject { ["items"] = items };

        Assert.Equal("0,1,2,", await RenderAsync("{% for i in items limit: 3 %}{{ i }},{% endfor %}", own));
        Assert.Equal(3, conversions[0]);

        conversions[0] = 0;
        Assert.Equal("10,11,", await RenderAsync("{% for i in items offset: 10 limit: 2 %}{{ i }},{% endfor %}", own));
        Assert.Equal(2, conversions[0]);

        conversions[0] = 0;
        Assert.Equal("0 999 5", await RenderAsync("{{ items | first }} {{ items | last }} {{ items | slice: 5 }}", own));
        Assert.Equal(3, conversions[0]);
    }

    [Fact]
    public async Task ArraysEqualFluidArraysWithTheSameItems()
    {
        var own = new NodeObject
        {
            ["items"] = new NodeArray(NodeValue.Create("a"), NodeValue.Create("b")),
            ["none"] = new NodeArray(),
        };

        Assert.Equal("yes yes yes", await RenderAsync("{% assign split = 'a,b' | split: ',' %}{% if items == split %}yes{% endif %} {% if split == items %}yes{% endif %} {% if none == empty %}yes{% endif %}", own));
    }

    // A node that renders as its number and counts how many times it is converted to a Fluid value.
    private sealed class CountingNode(int value, int[] conversions) : Node
    {
        public override T GetValue<T>() => throw new NotSupportedException();

        public override Node Clone() => this;

        public override bool Equals(Node? other) => ReferenceEquals(this, other);

        public override int GetHashCode() => value;

        public override string ToString()
        {
            Interlocked.Increment(ref conversions[0]);
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
