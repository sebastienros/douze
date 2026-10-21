using Douze.FileSystems;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow.Steps;

public class DefaultWorkflowTests : SiteTestBase
{
    [Fact]
    public async Task ToYamlWithContentFolder()
    {
        var source = new MemoryFileSystem();
        await source.CreateDirectoryAsync("/_content/");
        await source.CreateDirectoryAsync("/_data/");
        await source.CreateDirectoryAsync("/_includes/");

        var site = await SiteOptions.DetectAsync(source, new MemoryFileSystem(), null, null);

        // Section 4.6 of the workflow engine plan.
        Assert.Equal("""
            steps:
              site-data:   { inputs: "_data/**/*.{yml,yaml,json,xml}", uses: data, with: { scope: site } }
              folder-data: { inputs: "_content/**/*.{yml,yaml,json,xml}", uses: data, with: { scope: folder, root: _content } }
              pages:       { inputs: ["_content/**", "!_content/**/*.{yml,yaml,json,xml}"], needs: [site-data, folder-data], uses: frontmatter }
              permalink:   { needs: pages, uses: permalink, with: { root: _content } }
              collections: { needs: permalink, uses: collect }
              paginate:    { needs: [permalink, collections], uses: paginate }
              render:      { needs: paginate, uses: liquid }
              markdown:    { needs: render, uses: markdown }
              layout:      { needs: markdown, uses: layout }
              write:       { needs: layout, uses: write }

            """, WorkflowGraph.Build(DefaultWorkflow.Create(site)).ToYaml());
    }

    [Fact]
    public async Task ToYamlWithoutContentFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "douze-site");
        var source = new MemoryFileSystem();
        await source.CreateDirectoryAsync("/_data/");
        await source.CreateDirectoryAsync("/_includes/");

        // The output folder is inside the source folder.
        var site = await SiteOptions.DetectAsync(source, new MemoryFileSystem(), root, Path.Combine(root, "_site"));

        Assert.Equal("""
            steps:
              site-data:   { inputs: "_data/**/*.{yml,yaml,json,xml}", uses: data, with: { scope: site } }
              folder-data: { inputs: ["**/*.{yml,yaml,json,xml}", "!_data/**", "!_includes/**", "!douze.yml", "!_site/**"], uses: data, with: { scope: folder } }
              pages:       { inputs: ["**", "!**/*.{yml,yaml,json,xml}", "!_data/**", "!_includes/**", "!douze.yml", "!_site/**"], needs: [site-data, folder-data], uses: frontmatter }
              permalink:   { needs: pages, uses: permalink }
              collections: { needs: permalink, uses: collect }
              paginate:    { needs: [permalink, collections], uses: paginate }
              render:      { needs: paginate, uses: liquid }
              markdown:    { needs: render, uses: markdown }
              layout:      { needs: markdown, uses: layout }
              write:       { needs: layout, uses: write }

            """, WorkflowGraph.Build(DefaultWorkflow.Create(site)).ToYaml());
    }

    [Fact]
    public async Task LiquidRunsBeforeMarkdown()
    {
        await Source.CreateTemplateAsync("/a.md", "{% if bold %}**{{ word }}**{% endif %}", new { bold = true, word = "hello" });

        await BuildSuccessfullyAsync();

        Assert.Equal("<p><strong>hello</strong></p>\n", await Output.ReadAllTextAsync("/a/index.html"));
    }

    [Fact]
    public async Task LiquidCanBeTurnedOffPerPageOrFolder()
    {
        await Source.CreateTemplateAsync("/a.md", "`{{ word }}`", new { liquid = false, word = "hello" });
        await Source.CreateTemplateAsync("/docs/b.md", "{% raw %}", new { word = "hello" });
        await Source.WriteTextFileAsync("/docs/docs.yml", "liquid: false");
        await Source.CreateTemplateAsync("/c.html", "{{ word }}", new { word = "hello" });

        await BuildSuccessfullyAsync();

        Assert.Equal("<p><code>{{ word }}</code></p>\n", await Output.ReadAllTextAsync("/a/index.html"));
        Assert.Equal("<p>{% raw %}</p>\n", await Output.ReadAllTextAsync("/docs/b/index.html"));
        Assert.Equal("hello", await Output.ReadAllTextAsync("/c/index.html"));
    }

    [Fact]
    public async Task MarkdownPagesHaveHtmlUrls()
    {
        await Source.CreateTemplateAsync("/post.md", "{{ url }}");
        await Source.CreateTemplateAsync("/feed.md", "**feed**", new { permalink = "feed.xml" });

        var result = await BuildSuccessfullyAsync();

        Assert.Equal(["/feed.xml", "/post/index.html"], result.Outputs.Select(x => x.Path.FullName));
        Assert.Equal("<p>/post/index.html</p>\n", await Output.ReadAllTextAsync("/post/index.html"));

        // A Markdown page whose permalink is not HTML is not converted (and the default workflow's Liquid step does not
        // match .xml files).
        Assert.Equal("**feed**", await Output.ReadAllTextAsync("/feed.xml"));
    }

    [Fact]
    public async Task PagesCanPaginateCollections()
    {
        for (var i = 1; i <= 3; i++)
        {
            await Source.CreateTemplateAsync($"/posts/p{i}.md", "Post", new { tags = "post", title = $"P{i}", date = $"2020-01-0{i}" });
        }

        var blog = "{% for post in pagination.items %}{{ post.data.title }} {{ post.data.url }},{% endfor %}{{ url }}";
        await Source.CreateTemplateAsync("/blog.html", blog, new { pagination = new { data = "collections.post", size = 2 } });

        var result = await BuildSuccessfullyAsync();

        Assert.Equal("P1 /posts/p1/index.html,P2 /posts/p2/index.html,/blog/index.html", await Output.ReadAllTextAsync("/blog/index.html"));
        Assert.Equal("P3 /posts/p3/index.html,/blog/1/index.html", await Output.ReadAllTextAsync("/blog/1/index.html"));

        // The paginated page is in collections.all once, unpaginated, as in Eleventy.
        Assert.Equal(["/blog/index.html", "/posts/p1/index.html", "/posts/p2/index.html", "/posts/p3/index.html"],
            result.Collection("all").Select(x => x.Own["url"]!.GetValue<string>()).Order(StringComparer.Ordinal));
    }
}
