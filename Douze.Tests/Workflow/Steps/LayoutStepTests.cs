using Douze.FileSystems;
using Douze.Nodes;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow.Steps;

public class LayoutStepTests : StepTestBase
{
    private WorkflowDefinition Layout() => Workflow(
        Step("data", "data", inputs: ["_data/**"], with: With(("scope", NodeValue.Create("site")))),
        Step("folders", "data", inputs: ["content/**/*.yml"], with: With(("scope", NodeValue.Create("folder")), ("root", NodeValue.Create("content")))),
        Step("pages", "frontmatter", inputs: ["content/**/*.html"], needs: ["data", "folders"]),
        Step("layout", "layout", needs: ["pages"]));

    [Fact]
    public async Task FallbackPrecedence()
    {
        await Source.WriteTextFileAsync("/_data/layout.yml", "page.html");
        await Source.WriteTextFileAsync("/_data/title.yml", "Site title");
        await Source.WriteTextFileAsync("/_includes/page.html", "---\ntitle: Layout title\nsubtitle: Layout subtitle\nfooter: Layout footer\n---\n{{ title }}|{{ subtitle }}|{{ footer }}|{{ content }}");
        await Source.WriteTextFileAsync("/content/blog/blog.yml", "subtitle: Folder subtitle");
        await Source.WriteTextFileAsync("/content/blog/a.html", "---\ntitle: Own title\n---\n<p>A</p>");
        await Source.WriteTextFileAsync("/content/b.html", "<p>B</p>");

        var result = await BuildSuccessfullyAsync(Layout());

        var outputs = Outputs(result["layout"]);

        // Own, then folder, then site, then the layout's front matter.
        Assert.Equal("Site title|Layout subtitle|Layout footer|<p>B</p>", await TextAsync(outputs.Single(x => x.Path == "/content/b.html")));
        Assert.Equal("Own title|Folder subtitle|Layout footer|<p>A</p>", await TextAsync(outputs.Single(x => x.Path == "/content/blog/a.html")));
    }

    [Fact]
    public async Task Chaining()
    {
        await Source.WriteTextFileAsync("/_includes/post.html", "---\nlayout: base.html\ntitle: Post\n---\n<article>{{ content }}</article>");
        await Source.WriteTextFileAsync("/_includes/base.html", "---\ntitle: Base\nlang: en\n---\n<html lang=\"{{ lang }}\"><title>{{ title }}</title>{{ content }}</html>");
        await Source.WriteTextFileAsync("/content/a.html", "---\nlayout: post.html\n---\n<p>&amp;</p>");

        var result = await BuildSuccessfullyAsync(Layout());

        // The inner layout's front matter wins over the outer one's; content is not encoded again.
        Assert.Equal("<html lang=\"en\"><title>Post</title><article><p>&amp;</p></article></html>", await TextAsync(Outputs(result["layout"]).Single()));
        Assert.Equal(["/_includes/base.html", "/_includes/post.html"], result["layout"].Results.Single().Dependencies.Templates.Select(x => x.Path.FullName));
    }

    [Fact]
    public async Task MissingLayoutErrorText()
    {
        await Source.CreateDirectoryAsync("/_includes/");
        await Source.WriteTextFileAsync("/content/index.html", "---\nlayout: x.html\n---\nhome");

        var result = await BuildAsync(Layout());

        var error = Assert.Single(result.Errors);
        Assert.Equal("error: layout: /content/index.html: layout 'x.html' not found in '/_includes/' (used by '/content/index.html')", error.ToString());
        Assert.Equal(Douze.Workflow.Scheduling.StepState.Failed, result["layout"].State);
    }

    [Fact]
    public async Task ChainLimit()
    {
        await Source.WriteTextFileAsync("/_includes/a.html", "---\nlayout: b.html\n---\na{{ content }}");
        await Source.WriteTextFileAsync("/_includes/b.html", "---\nlayout: a.html\n---\nb{{ content }}");
        await Source.WriteTextFileAsync("/content/index.html", "---\nlayout: a.html\n---\nhome");

        var result = await BuildAsync(Layout());

        var error = Assert.Single(result.Errors);
        Assert.StartsWith($"layout chain is longer than {Douze.Workflow.Steps.LayoutStep.MaxDepth}: a.html -> b.html -> a.html -> ", error.Message);
    }
}
