using Douze.FileSystems;
using Douze.Nodes;
using Douze.Workflow;
using Fluid;
using Xunit;

namespace Douze.Tests.Liquid;

public class NodeValuesTests
{
    private static Task<string> RenderAsync(string template, NodeObject own) => MetadataValueTests.RenderAsync(template, new Metadata(own, "/"), ScopeSet.Empty);

    [Fact]
    public async Task DateTimeRendersLikeTheProcessorPipeline()
    {
        // page.date reaches templates as a NodeValue<DateTime> holding the file's UTC modification time. The processor
        // pipeline passes the NodeObject to Fluid as is; the workflow engine converts it explicitly. Both must render
        // the same with the formats the Sample uses (open question 4 of the workflow engine plan).
        var options = new Options(new MemoryFileSystem(), new MemoryFileSystem());

        foreach (var date in new[] { new DateTime(2020, 1, 1, 23, 59, 30, DateTimeKind.Utc), new DateTime(2019, 12, 31, 0, 0, 1, DateTimeKind.Utc), new DateTime(2024, 7, 14, 12, 0, 0, 123, DateTimeKind.Utc) })
        {
            foreach (var template in new[] { "{{ d | date: '%Y/%m/%d %H:%M:%S' }}", "{{ d | date: '%c' }}", "{{ d | date: '%Y/%m/%d' }}" })
            {
                var data = new NodeObject { ["d"] = NodeValue.Create(date) };

                var processors = await options.FluidParser.Parse(template).RenderAsync(new TemplateContext(data, options.TemplateOptions));
                var engine = await RenderAsync(template, data);

                Assert.Equal(processors, engine);
            }
        }
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
