using Douze.FileSystems;
using Douze.Nodes;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow.Steps;

public class HeadingsStepTests : StepTestBase
{
    private static WorkflowDefinition Headings(NodeObject? with = null) => Workflow(
        Step("pages", "frontmatter", inputs: ["**"]),
        Step("markdown", "markdown", needs: ["pages"]),
        Step("headings", "headings", needs: ["markdown"], with: with));

    private async Task<VirtualFile> BuildPageAsync(string markdown, NodeObject? with = null)
    {
        await Source.WriteTextFileAsync("/page.md", markdown);
        var result = await BuildSuccessfullyAsync(Headings(with));
        return Assert.Single(Outputs(result["headings"]));
    }

    [Fact]
    public async Task AssignsUniqueIdsAndKeepsExistingOnes()
    {
        var file = await BuildPageAsync("# Title\n\n## Setup\n\n## Setup\n\n<h2 id=\"custom\">Custom</h2>\n\n## Custom\n");

        Assert.Equal(
            "<h1 id=\"title\">Title</h1>\n<h2 id=\"setup\">Setup</h2>\n<h2 id=\"setup-1\">Setup</h2>\n<h2 id=\"custom\">Custom</h2>\n<h2 id=\"custom-1\">Custom</h2>\n",
            await TextAsync(file));
    }

    [Fact]
    public async Task HeadingsAndTocMetadata()
    {
        var file = await BuildPageAsync("# Title\n\n## A\n\n### A.1\n\n#### Deep\n\n### A.2\n\n## B &amp; *C*\n");

        var headings = (NodeArray)file.Metadata.Own["headings"]!;
        Assert.Equal(
            ["1:Title:title", "2:A:a", "3:A.1:a1", "4:Deep:deep", "3:A.2:a2", "2:B & C:b--c"],
            headings.Cast<NodeObject>().Select(x => $"{x["depth"]!.GetValue<int>()}:{x["text"]!.GetValue<string>()}:{x["id"]!.GetValue<string>()}"));

        Assert.Equal("[A [A.1, A.2], B & C]", Tree((NodeArray)file.Metadata.Own["toc"]!));
        Assert.Equal("Title", ((NodeObject)file.Metadata.Own["page"]!)["heading"]!.GetValue<string>());
    }

    [Fact]
    public async Task Levels()
    {
        var file = await BuildPageAsync("# T\n\n## A\n\n### A.1\n\n#### Deep\n", With(("levels", new NodeArray { NodeValue.Create(2), NodeValue.Create(4) })));

        Assert.Equal("[A [Deep]]", Tree((NodeArray)file.Metadata.Own["toc"]!));
    }

    [Fact]
    public async Task InvalidLevels()
    {
        var error = Assert.Throws<WorkflowValidationException>(() => WorkflowGraph.Build(Headings(With(("levels", NodeValue.Create(7))))));

        Assert.Equal("option 'levels' of step 'headings' must be a list of heading levels from 1 to 6", error.Errors.Single().Message);
    }

    [Fact]
    public async Task Anchors()
    {
        var file = await BuildPageAsync("## Setup\n", With(("anchors", NodeValue.Create(true)), ("anchor-class", NodeValue.Create("anchor")), ("anchor-label", NodeValue.Create("Link to \"section\""))));

        Assert.Equal("<h2 id=\"setup\">Setup<a class=\"anchor\" href=\"#setup\" aria-label=\"Link to &quot;section&quot;\">#</a></h2>\n", await TextAsync(file));
    }

    [Fact]
    public async Task HeadingsOfARawRegionAndOfCodeBlocks()
    {
        var file = await BuildPageAsync("## Page\n\n\u001E\n<div>\n\n<h2>From a shortcode</h2>\n\n</div>\n\u001E\n\n```html\n<h2>Not a heading</h2>\n```\n");

        var headings = (NodeArray)file.Metadata.Own["headings"]!;
        Assert.Equal(["page", "from-a-shortcode"], headings.Cast<NodeObject>().Select(x => x["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task NoPageHeadingWithoutH1()
    {
        var file = await BuildPageAsync("## Only\n");

        Assert.False(((NodeObject)file.Metadata.Own["page"]!).ContainsKey("heading"));
    }

    [Fact]
    public async Task FileWithoutHeadingsIsUnchanged()
    {
        var file = await BuildPageAsync("Text\n");

        Assert.False(file.Metadata.Own.ContainsKey("headings"));
        Assert.Equal("markdown", file.Provenance.Step);
    }

    [Fact]
    public async Task TocFilter()
    {
        await Source.WriteTextFileAsync("/page.md", "## A\n\n### A.1\n\n#### Deep\n\n## B &lt;x&gt;\n");
        await Source.WriteTextFileAsync("/_includes/toc.liquid", "{{ file.data.toc | toc: class: \"toc\", max-depth: 3 }}|{{ file.data.toc | toc }}|{{ nothing | toc }}");

        var result = await BuildSuccessfullyAsync(Workflow(
            Step("pages", "frontmatter", inputs: ["page.md"]),
            Step("markdown", "markdown", needs: ["pages"]),
            Step("headings", "headings", needs: ["markdown"], with: With(("levels", new NodeArray { NodeValue.Create(2), NodeValue.Create(3), NodeValue.Create(4) }))),
            Step("render", "liquid", needs: ["headings"], match: ["**/*.html"], with: With(("template", NodeValue.Create("toc.liquid")), ("permalink", NodeValue.Create("toc.html"))))));

        var html = await TextAsync(Assert.Single(Outputs(result["render"])));

        Assert.Equal(
            "<ul class=\"toc\"><li><a href=\"#a\">A</a><ul><li><a href=\"#a1\">A.1</a></li></ul></li><li><a href=\"#b-x\">B &lt;x&gt;</a></li></ul>"
            + "|<ul><li><a href=\"#a\">A</a><ul><li><a href=\"#a1\">A.1</a><ul><li><a href=\"#deep\">Deep</a></li></ul></li></ul></li><li><a href=\"#b-x\">B &lt;x&gt;</a></li></ul>|",
            html);
    }

    private static string Tree(NodeArray entries)
    {
        return "[" + string.Join(", ", entries.Cast<NodeObject>().Select(x =>
        {
            var children = (NodeArray)x["children"]!;
            var text = x["text"]!.GetValue<string>();
            return children.Count == 0 ? text : $"{text} {Tree(children)}";
        })) + "]";
    }
}
