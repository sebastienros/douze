using System.Text.RegularExpressions;
using Douze.FileSystems;
using Douze.Images;
using Douze.Nodes;
using Douze.Tests.Images;
using Douze.Tests.Workflow.Cache;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow.Steps;

public partial class ImagesStepTests : CacheTestBase
{
    private static WorkflowDefinition Pictures(NodeObject? with = null) => StepTestBase.Workflow(
        StepTestBase.Step("pages", "frontmatter", inputs: ["docs/**/*.md"]),
        StepTestBase.Step("markdown", "markdown", needs: ["pages"]),
        StepTestBase.Step("images", "images", inputs: ["docs/**/*.{png,svg}"], with: with ?? StepTestBase.With(
            ("widths", new NodeArray { NodeValue.Create(100), NodeValue.Create(200) }),
            ("formats", new NodeArray { NodeValue.Create("webp") }))),
        StepTestBase.Step("image-map", "image-map", needs: ["images"]),
        StepTestBase.Step("pictures", "pictures", needs: ["markdown", "image-map"]),
        StepTestBase.Step("write", "write", needs: ["pictures", "images"]));

    private static string Normalize(string html) => Hash().Replace(html, ".HASH.");

    [GeneratedRegex(@"\.[0-9a-f]{8}\.")]
    private static partial Regex Hash();

    private async Task WriteSiteAsync()
    {
        await Source.WriteBinaryFileAsync("/docs/images/a.png", TestImages.Solid(300, 150, (0, 128, 0)));
        await Source.WriteTextFileAsync("/docs/images/b.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"48\" height=\"24\"></svg>");
        await Source.WriteTextFileAsync("/docs/page.md", "![Alt *text*](images/a.png \"A & B\")\n\n![B](/docs/images/b.svg)\n\n![External](https://example.com/c.png) ![Missing](images/none.png)\n");
    }

    [Fact]
    public async Task VariantsMetadataAndPictures()
    {
        await WriteSiteAsync();

        var result = await BuildCachedAsync(Pictures(), cache: false);

        // Variants and the fingerprinted originals, instead of the images.
        Assert.Equal(
            ["/assets/images/a-100.HASH.webp", "/assets/images/a-200.HASH.webp", "/assets/images/a.HASH.png", "/assets/images/b.HASH.svg", "/docs/page.html"],
            result.Outputs.Select(x => Normalize(x.Path.FullName)));

        var outputs = StepTestBase.Outputs(result["images"]);
        var image = ImageSet.FromNode(outputs[0].Metadata.Own["image"])!;
        Assert.Equal(("/docs/images/a.png", 300, 150, "png"), (image.Source, image.Width, image.Height, image.Format));
        Assert.Equal(["webp 100x50", "webp 200x100"], image.Variants.Select(x => $"{x.Format} {x.Width}x{x.Height}"));
        Assert.Equal(outputs[2].Path.FullName, image.Url);
        Assert.Same(outputs[0].Metadata.Own["image"], outputs[1].Metadata.Own["image"]);

        // image-map contributes images by source url, and emits nothing.
        Assert.Empty(StepTestBase.Outputs(result["image-map"]));
        var images = (NodeObject)result["image-map"].Contributions.Site.Single().Values["images"]!;
        Assert.Equal(["/docs/images/a.png", "/docs/images/b.svg"], images.Select(x => x.Key));

        Assert.Equal(
            "<p><img src=\"/assets/images/a-200.HASH.webp\" srcset=\"/assets/images/a-100.HASH.webp 100w, /assets/images/a-200.HASH.webp 200w\" width=\"200\" height=\"100\" alt=\"Alt text\" loading=\"lazy\" decoding=\"async\" title=\"A &amp; B\"></p>\n"
            + "<p><img src=\"/assets/images/b.HASH.svg\" width=\"48\" height=\"24\" alt=\"B\" loading=\"lazy\" decoding=\"async\"></p>\n"
            + "<p><img src=\"https://example.com/c.png\" alt=\"External\" /> <img src=\"images/none.png\" alt=\"Missing\" /></p>\n",
            Normalize(await Output.ReadAllTextAsync("/docs/page.html")));
    }

    [Fact]
    public async Task APageDependsOnTheImagesItUses()
    {
        await WriteSiteAsync();
        await Source.WriteTextFileAsync("/docs/other.md", "no images");
        await Source.WriteTextFileAsync("/docs/svg.md", "![B](images/b.svg)");

        await BuildCachedAsync(Pictures());

        // a.png changes: only the page that shows it is rewritten.
        await Source.WriteBinaryFileAsync("/docs/images/a.png", TestImages.Solid(300, 150, (255, 0, 0)));
        var second = await BuildCachedAsync(Pictures());

        Assert.Equal(["/docs/images/a.png"], Executed(second, "images"));
        Assert.Equal(["/docs/page.html"], Executed(second, "pictures"));
    }

    [Fact]
    public async Task DefaultsAndInvalidOptions()
    {
        await Source.WriteBinaryFileAsync("/docs/a.jpg", TestImages.Solid(40, 20, (0, 0, 0), ".jpg"));

        var result = await BuildCachedAsync(StepTestBase.Workflow(StepTestBase.Step("images", "images", inputs: ["**"])), cache: false);

        // No widths nor formats: the source's own, and the original.
        Assert.Equal(["/assets/images/a-40.HASH.jpg", "/assets/images/a.HASH.jpg"], StepTestBase.Outputs(result["images"]).Select(x => Normalize(x.Path.FullName)));

        var error = Assert.Throws<WorkflowValidationException>(() => WorkflowGraph.Build(StepTestBase.Workflow(
            StepTestBase.Step("images", "images", inputs: ["**"], with: StepTestBase.With(("formats", new NodeArray { NodeValue.Create("bmp") }))))));
        Assert.Equal("option 'formats' of step 'images' must be a list of image formats ('webp', 'avif', 'png' or 'jpeg')", error.Errors.Single().Message);
    }
}
