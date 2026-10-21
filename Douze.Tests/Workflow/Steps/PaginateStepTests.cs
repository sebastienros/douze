using Douze.FileSystems;
using Douze.Nodes;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow.Steps;

public class PaginateStepTests : StepTestBase
{
    private static WorkflowDefinition Paginate() => Workflow(
        Step("pages", "frontmatter", inputs: ["**"]),
        Step("paginate", "paginate", needs: ["pages"]));

    private const string Letters = "---\npagination:\n  data: letters\n  size: 2\nletters: [a, b, c, d, e]\n---\n\n";

    [Fact]
    public async Task Hrefs()
    {
        await Source.WriteTextFileAsync("/letters.html", Letters);

        var result = await BuildSuccessfullyAsync(Paginate());

        var pages = Outputs(result["paginate"]);
        string[] expected = ["/letters/index.html", "/letters/1/index.html", "/letters/2/index.html"];

        foreach (var page in pages)
        {
            var pagination = page.Metadata.Own["pagination"]!;
            Assert.Equal(expected, pagination["hrefs"]!.AsArray().Select(x => x!.GetValue<string>()));
            Assert.Equal(expected[pagination["pageNumber"]!.GetValue<int>()], pagination["pages"]![pagination["pageNumber"]!.GetValue<int>()]!["href"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task PerPagePermalinkWithAlias()
    {
        await Source.WriteTextFileAsync("/products.html", "---\npagination:\n  data: products\n  size: 1\n  alias: product\nproducts: [shoes, hats]\npermalink: \"shop/{{ product }}/\"\n---\n{{ product }}");

        var result = await BuildSuccessfullyAsync(Paginate());

        var pages = Outputs(result["paginate"]);
        Assert.Equal(["/shop/hats/index.html", "/shop/shoes/index.html"], pages.Select(x => x.Path.FullName));

        // The resolved permalink is stored, so the permalink step keeps it.
        Assert.Equal("/shop/hats/index.html", pages[0].Metadata.Own["permalink"]!.GetValue<string>());
        Assert.Equal("hats", pages[0].Metadata.Own["product"]!.GetValue<string>());
    }

    [Fact]
    public async Task DefaultPagedPaths()
    {
        await Source.WriteTextFileAsync("/letters.html", Letters);
        await Source.WriteTextFileAsync("/blog/index.html", Letters);
        await Source.WriteTextFileAsync("/fixed.html", Letters.Replace("---\n\n", "permalink: archive/\n---\n"));

        var result = await BuildSuccessfullyAsync(Workflow(
            Step("pages", "frontmatter", inputs: ["**"]),
            Step("paginate", "paginate", needs: ["pages"]),
            Step("permalink", "permalink", needs: ["paginate"])));

        Assert.Equal(
            [
                "/archive/1/index.html", "/archive/2/index.html", "/archive/index.html",
                "/blog/1/index.html", "/blog/2/index.html", "/blog/index.html",
                "/letters/1/index.html", "/letters/2/index.html", "/letters/index.html",
            ],
            Outputs(result["permalink"]).Select(x => x.Path.FullName));
    }

    [Fact]
    public async Task SharedPaginationObjects()
    {
        await Source.WriteTextFileAsync("/letters.html", Letters);

        var result = await BuildSuccessfullyAsync(Paginate());

        var paginations = Outputs(result["paginate"]).Select(x => (NodeObject)x.Metadata.Own["pagination"]!).OrderBy(x => x["pageNumber"]!.GetValue<int>()).ToArray();

        Assert.Equal(3, paginations.Length);
        Assert.Equal([["a", "b"], ["c", "d"], ["e"]], paginations.Select(x => x["items"]!.AsArray().Select(i => i!.GetValue<string>()).ToArray()));

        // Every page links the same page summaries.
        Assert.Same(paginations[0]["pages"], paginations[2]["pages"]);
        Assert.Same(paginations[0]["pages"]![1], paginations[0]["page"]!["next"]);
        Assert.Same(paginations[1]["page"]!["previous"], paginations[2]["page"]!["first"]);
        Assert.Null(paginations[2]["page"]!["next"]);

        // The items are shared with the source data, not copied.
        var source = Outputs(result["pages"]).Single().Metadata.Own["letters"]!.AsArray();
        Assert.Same(source[0], paginations[0]["items"]![0]);

        // Linked pages form no cycle: every page's metadata can be hashed.
        Assert.All(Outputs(result["paginate"]), x => _ = x.MetadataHash);
    }
}
