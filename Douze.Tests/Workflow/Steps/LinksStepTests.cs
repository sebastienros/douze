using Douze.FileSystems;
using Douze.Markdown;
using Douze.Nodes;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow.Steps;

public class LinksStepTests : StepTestBase
{
    private static WorkflowDefinition Links() => Workflow(
        Step("pages", "frontmatter", inputs: ["**"]),
        Step("links", "links", needs: ["pages"]));

    private async Task<VirtualFile> BuildPageAsync(string html)
    {
        await Source.WriteTextFileAsync("/page.html", html);
        var result = await BuildSuccessfullyAsync(Links());
        return Assert.Single(Outputs(result["links"]));
    }

    private static string[] LinksOf(VirtualFile file) =>
        [.. ((NodeArray)file.Metadata.Own["links"]!).Cast<NodeObject>().Select(x => $"{x["line"]!.GetValue<int>()}:{x["url"]!.GetValue<string>()}")];

    private static string[] AnchorsOf(VirtualFile file) => [.. ((NodeArray)file.Metadata.Own["anchors"]!).Select(x => x!.GetValue<string>())];

    [Fact]
    public async Task ExtractsTheLinkAttributesWithTheirLines()
    {
        const string Html = """
            <!doctype html>
            <link rel="stylesheet" href="/css/site.css">
            <a href='b/'>B</a> <a HREF=c.html>C</a>
            <img src="/img/a.png"
                 data-src="/img/lazy.png">
            <video poster="/img/poster.jpg"><source src="/v.mp4"></video>
            <a href="/docs/a/?x=1&amp;y=2#setup">entities</a>
            <a href="  ">blank</a> <a href="/css/site.css">again</a>
            """;
        var file = await BuildPageAsync(Html);

        Assert.Equal(
            ["2:/css/site.css", "3:b/", "3:c.html", "4:/img/a.png", "5:/img/lazy.png", "6:/img/poster.jpg", "6:/v.mp4", "7:/docs/a/?x=1&y=2#setup", "8:/css/site.css"],
            LinksOf(file));
        Assert.Equal(Html, await TextAsync(file));
    }

    [Fact]
    public async Task SrcsetCandidates()
    {
        var file = await BuildPageAsync("<img srcset=\"/a-480.webp 480w, /a-960.webp 960w,/a,b.png 2x, /c.png\">");

        Assert.Equal(["1:/a,b.png", "1:/a-480.webp", "1:/a-960.webp", "1:/c.png"], LinksOf(file));
        // A url runs to whitespace: without one after the comma, it holds the comma.
        Assert.Equal(["/a.png", "/b.png"], LinkScanner.SrcsetUrls("/a.png 1x,/b.png 2x"));
        Assert.Equal(["/a.png,/b.png"], LinkScanner.SrcsetUrls("/a.png,/b.png"));
    }

    [Fact]
    public async Task AnchorsAreIdsAndNamesOfAnchors()
    {
        var file = await BuildPageAsync("<h2 id=\"setup\">S</h2><a name=\"old\"></a><input name=\"q\"><div id=\"a&amp;b\"></div><p id=\"setup\"></p>");

        Assert.Equal(["a&b", "old", "setup"], AnchorsOf(file));
    }

    [Fact]
    public async Task CommentsScriptsAndStylesAreSkipped()
    {
        var file = await BuildPageAsync("""
            <!-- <a href="/commented"> -->
            <script src="/app.js">document.write('<a href="/scripted">');</script>
            <style>a[href="/styled"] {}</style>
            <pre><code><span class="line">&lt;a href="/escaped"&gt;</span><a href="/real">x</a></code></pre>
            """);

        Assert.Equal(["2:/app.js", "4:/real"], LinksOf(file));
    }
}
