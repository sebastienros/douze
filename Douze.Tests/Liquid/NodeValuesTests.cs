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
}
