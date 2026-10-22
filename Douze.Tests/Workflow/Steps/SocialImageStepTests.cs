using Douze.FileSystems;
using Douze.Nodes;
using Douze.Tests.Images;
using Douze.Workflow;
using NetVips;
using Xunit;

namespace Douze.Tests.Workflow.Steps;

public class SocialImageStepTests : StepTestBase
{
    private static NodeObject Block(string value, string font, int x, int y, int width, int height, string color, string? fontFile, string align = "left")
    {
        var block = new NodeObject
        {
            ["value"] = NodeValue.Create(value),
            ["font"] = NodeValue.Create(font),
            ["x"] = NodeValue.Create(x),
            ["y"] = NodeValue.Create(y),
            ["width"] = NodeValue.Create(width),
            ["height"] = NodeValue.Create(height),
            ["color"] = NodeValue.Create(color),
            ["align"] = NodeValue.Create(align),
        };

        if (fontFile != null)
        {
            block["fontfile"] = NodeValue.Create(fontFile);
        }

        return block;
    }

    private static WorkflowDefinition Og(NodeObject with) => Workflow(
        Step("pages", "frontmatter", inputs: ["docs/**/*.md"]),
        Step("og", "social-image", needs: ["pages"], with: with),
        Step("markdown", "markdown", needs: ["og"]),
        Step("layout", "layout", needs: ["markdown"]),
        Step("write", "write", needs: ["layout"]));

    private async Task<NodeObject> WriteSiteAsync(bool withFont = true)
    {
        TestImages.RequireLibvips();

        await Source.WriteTextFileAsync("/_includes/og/card.svg.liquid", "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"1200\" height=\"630\"><rect width=\"1200\" height=\"630\" fill=\"{{ background }}\"/><image xlink:href=\"logo.svg\" x=\"20\" y=\"20\" width=\"100\" height=\"100\"/></svg>");
        await Source.WriteTextFileAsync("/_includes/og/logo.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\" fill=\"#ff0000\"/></svg>");
        await Source.WriteTextFileAsync("/_includes/page.html", "<html><head><meta property=\"og:image\" content=\"{{ page | social_image_url: 'og/{{ slug }}.png' }}\"></head><body>{{ content }}</body></html>");
        await Source.WriteTextFileAsync("/docs/a.md", "---\nslug: a\ntitle: Getting started & more\ndescription: The *first* page\nbackground: \"#204080\"\nlayout: page.html\n---\nA");
        await Source.WriteTextFileAsync("/docs/b.md", "---\nslug: b\ntitle: Skipped\nog: false\nbackground: \"#000000\"\nlayout: page.html\n---\nB");

        string? fontFile = null;

        if (withFont && TestImages.FontFile() is { } font)
        {
            await Source.WriteBinaryFileAsync("/assets/fonts/font.ttf", await File.ReadAllBytesAsync(font, TestContext.Current.CancellationToken));
            fontFile = "assets/fonts/font.ttf";
        }

        return With(
            ("template", NodeValue.Create("og/card.svg.liquid")),
            ("output", NodeValue.Create("og/{{ slug }}.png")),
            ("skip", NodeValue.Create("og == false")),
            ("text", new NodeArray
            {
                Block("{{ title }}", "sans bold 60", 80, 160, 1040, 220, "#ffffff", fontFile),
                Block("{{ description }}", "sans 30", 80, 400, 1040, 150, "#ffff00", fontFile, "center"),
            }));
    }

    [Fact]
    public async Task RendersTheTemplateAndTheTextBlocks()
    {
        var result = await BuildSuccessfullyAsync(Og(await WriteSiteAsync()));

        Assert.Equal(["/docs/a.html", "/docs/b.html", "/og/a.png"], result.Outputs.Select(x => x.Path.FullName));

        var png = await Output.ReadAllBytesAsync("/og/a.png");
        Assert.Equal((1200, 630, "pngload_buffer", 4), TestImages.Describe(png));

        // The background of the template, the logo it references (inlined), and the text of the blocks.
        Assert.Equal([32, 64, 128, 255], TestImages.Pixel(png, 600, 610));
        Assert.Equal([255, 0, 0, 255], TestImages.Pixel(png, 70, 70));

        // Without a font file (a machine without fonts), Pango may render nothing.
        if (TestImages.FontFile() != null)
        {
            using var image = Image.NewFromBuffer(png);
            using var title = image.Crop(80, 160, 1040, 220);
            using var description = image.Crop(80, 400, 1040, 150);
            Assert.True(title.ExtractBand(2).Max() > 240, "no white title");
            Assert.True(description.ExtractBand(0).Max() > 240 && description.ExtractBand(1).Max() > 240, "no yellow description");
        }

        // The page goes through as it is; the image is an output of its unit; the layout finds the same url.
        var unit = result["og"].Results.Single(x => x.Key.Unit == "/docs/a.md");
        Assert.Equal(["/docs/a.md", "/og/a.png"], unit.Files.Select(x => x.Path.FullName));
        Assert.Equal("pages", unit.Files[0].Provenance.Step);
        Assert.Contains("/_includes/og/logo.svg", unit.Dependencies.Templates.Select(x => x.Path.FullName));
        Assert.Contains("<meta property=\"og:image\" content=\"/og/a.png\">", await Output.ReadAllTextAsync("/docs/a.html"));
    }

    [Fact]
    public async Task SkippedPagesGetNoImage()
    {
        var result = await BuildSuccessfullyAsync(Og(await WriteSiteAsync(withFont: false)));

        var unit = result["og"].Results.Single(x => x.Key.Unit == "/docs/b.md");
        Assert.Equal(["/docs/b.md"], unit.Files.Select(x => x.Path.FullName));
        Assert.False((await Output.GetFileEntryAsync("/og/b.png")).Exists);
    }

    [Fact]
    public async Task MissingFontFile()
    {
        var with = await WriteSiteAsync(withFont: false);
        ((NodeObject)((NodeArray)with["text"]!)[0]!)["fontfile"] = NodeValue.Create("fonts/missing.ttf");

        var result = await BuildAsync(Og(with));

        var error = Assert.Single(result.Errors);
        Assert.Equal("og", error.Step);
        Assert.Equal("font file 'fonts/missing.ttf' not found", error.Message);
    }

    [Fact]
    public async Task InvalidOptions()
    {
        var error = Assert.Throws<WorkflowValidationException>(() => WorkflowGraph.Build(Og(With(
            ("template", NodeValue.Create("og.svg")),
            ("output", NodeValue.Create("og.png")),
            ("text", new NodeArray { new NodeObject { ["value"] = NodeValue.Create("x"), ["colour"] = NodeValue.Create("#fff") } })))));

        Assert.Equal("option 'text' of step 'og' has an unknown key 'colour'; did you mean 'color'?", error.Errors.Single().Message);

        var output = Assert.Throws<DouzeException>(() => Douze.Workflow.Steps.SocialImageStep.OutputPath("og/../x.png"));
        Assert.Equal("social image output 'og/../x.png' is not a file path", output.Message);
        Assert.Equal("/og/docs/x/index.png", Douze.Workflow.Steps.SocialImageStep.OutputPath(" /og//docs/x/index.png\n").FullName);
    }
}
