using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Douze.FileSystems;
using Douze.Images;
using Douze.Markdown;
using Douze.Tests.Images;
using Douze.Tests.Workflow.Cache;
using Douze.Workflow;
using Fluid;
using Xunit;

namespace Douze.Tests.Liquid.Tags;

public partial class ImageTagTests : CacheTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> RenderAsync(string page, string path = "/_content/docs/page.html")
    {
        await Source.WriteTextFileAsync(path, page);
        var result = await BuildCachedAsync(cache: false);
        var output = result.Outputs.Single(x => x.Path.FullName.EndsWith(".html", StringComparison.Ordinal)).Path;
        return await Output.ReadAllTextAsync(output);
    }

    private static string Normalize(string html) => Hash().Replace(html, ".HASH.");

    [GeneratedRegex(@"\.[0-9a-f]{8}\.")]
    private static partial Regex Hash();

    [Fact]
    public void TagsWithLiteralArgumentsArePrefetched()
    {
        var template = Douze.Liquid.LiquidOptions.CreateParser().Parse("""
            {% image "a.png", widths: [640, 1280], formats: ["webp"], alt: "A" %}
            {% if x %}{% for i in (1..2) %}{% image light: "b.png", dark: "c.png", quality: 80 %}{% endfor %}{% endif %}
            {% capture c %}{% image "d.png" %}{% endcapture %}
            {% image page.image %}
            {% image "e.png", widths: [w] %}
            """);

        var found = Douze.Liquid.Tags.ImageTag.FindLiteralTags(template);

        Assert.Equal(["a.png", "b.png", "d.png"], found.Select(x => ((Fluid.Ast.LiteralExpression)x[0].Value).Value.ToStringValue(new TemplateContext())));
    }

    [Fact]
    public async Task PictureWithSourcesSrcsetSizesAndTheSizeOfTheFallback()
    {
        await Source.WriteBinaryFileAsync("/_content/docs/images/dashboard.png", TestImages.Solid(1600, 900, (10, 120, 200)));

        var html = await RenderAsync("""{% image "images/dashboard.png", alt: "The dashboard", widths: [640, 1280], formats: ["webp", "png"], sizes: "(min-width: 60em) 50vw, 100vw", class: "figure" %}""");

        Assert.Equal(
            "<picture><source type=\"image/webp\" srcset=\"/assets/images/dashboard-640.HASH.webp 640w, /assets/images/dashboard-1280.HASH.webp 1280w\" sizes=\"(min-width: 60em) 50vw, 100vw\">"
            + "<img src=\"/assets/images/dashboard-1280.HASH.png\" srcset=\"/assets/images/dashboard-640.HASH.png 640w, /assets/images/dashboard-1280.HASH.png 1280w\" sizes=\"(min-width: 60em) 50vw, 100vw\" width=\"1280\" height=\"720\" alt=\"The dashboard\" class=\"figure\" loading=\"lazy\" decoding=\"async\"></picture>",
            Normalize(html));

        // The variants are written with the page, with the widths and formats asked for.
        var variants = Output.EnumerateFiles("/assets/images/").Order().ToArray();
        Assert.Equal(4, variants.Length);

        foreach (var variant in variants)
        {
            var (width, height, loader, _) = TestImages.Describe(await Output.ReadAllBytesAsync(variant));
            var name = Path.GetFileName(variant);
            Assert.Equal(name.Contains("-640.", StringComparison.Ordinal) ? (640, 360) : (1280, 720), (width, height));
            Assert.StartsWith(name.EndsWith(".webp", StringComparison.Ordinal) ? "webpload" : "pngload", loader);
        }
    }

    [Fact]
    public async Task SingleWidthAndFormatGivesAPlainImg()
    {
        await Source.WriteBinaryFileAsync("/_content/docs/a.png", TestImages.Solid(300, 200, (0, 0, 0)));

        var html = await RenderAsync("""{% image "a.png", alt: "A", widths: [100], formats: ["webp"], loading: "eager", data-zoom: true, title: "A & B", hidden: false %}""");

        Assert.Equal("<img src=\"/assets/images/a-100.HASH.webp\" width=\"100\" height=\"67\" alt=\"A\" loading=\"eager\" decoding=\"async\" data-zoom title=\"A &amp; B\">", Normalize(html));
    }

    [Fact]
    public async Task VariantsNeverWiderThanTheSource()
    {
        await Source.WriteBinaryFileAsync("/_content/docs/a.png", TestImages.Solid(800, 400, (0, 0, 0)));

        var html = await RenderAsync("""{% image "a.png", widths: [640, 1280, 2000], formats: ["png"] %}""");

        Assert.Equal("<img src=\"/assets/images/a-800.HASH.png\" srcset=\"/assets/images/a-640.HASH.png 640w, /assets/images/a-800.HASH.png 800w\" width=\"800\" height=\"400\" alt=\"\" loading=\"lazy\" decoding=\"async\">", Normalize(html));
    }

    [Fact]
    public async Task SvgIsCopiedWithTheSizeOfItsViewBox()
    {
        // No libvips needed: the size is read from the file.
        var svg = "<?xml version=\"1.0\"?>\n<!-- <svg width=\"1\"> -->\n<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 320 180\"><rect width=\"10\" height=\"10\"/></svg>";
        await Source.WriteTextFileAsync("/_content/images/arch.svg", svg);

        var html = await RenderAsync("""{% image "/images/arch.svg", alt: "Architecture", widths: [100], formats: ["webp"] %}""");

        var copy = Output.EnumerateFiles("/assets/images/").Single();
        Assert.Matches(@"^/assets/images/arch\.[0-9a-f]{8}\.svg$", copy);
        Assert.Equal(svg, await Output.ReadAllTextAsync(copy));
        Assert.Equal($"<img src=\"{copy}\" width=\"320\" height=\"180\" alt=\"Architecture\" loading=\"lazy\" decoding=\"async\">", html);
    }

    [Fact]
    public async Task LightAndDarkSources()
    {
        await Source.WriteBinaryFileAsync("/_content/docs/a-light.png", TestImages.Solid(400, 200, (255, 255, 255)));
        await Source.WriteBinaryFileAsync("/_content/docs/a-dark.png", TestImages.Solid(400, 200, (0, 0, 0)));

        var html = await RenderAsync("""{% image light: "a-light.png", dark: "a-dark.png", alt: "A", formats: ["webp", "png"] %}""");

        Assert.Equal(
            "<picture><source media=\"(prefers-color-scheme: dark)\" type=\"image/webp\" srcset=\"/assets/images/a-dark-400.HASH.webp\" width=\"400\" height=\"200\">"
            + "<source media=\"(prefers-color-scheme: dark)\" type=\"image/png\" srcset=\"/assets/images/a-dark-400.HASH.png\" width=\"400\" height=\"200\">"
            + "<source type=\"image/webp\" srcset=\"/assets/images/a-light-400.HASH.webp\">"
            + "<img src=\"/assets/images/a-light-400.HASH.png\" width=\"400\" height=\"200\" alt=\"A\" loading=\"lazy\" decoding=\"async\"></picture>",
            Normalize(html));
    }

    [Fact]
    public async Task ThemeAttributeRendersBothImages()
    {
        await Source.WriteBinaryFileAsync("/_content/docs/a-light.png", TestImages.Solid(40, 20, (255, 255, 255)));
        await Source.WriteBinaryFileAsync("/_content/docs/a-dark.png", TestImages.Solid(40, 20, (0, 0, 0)));

        var html = await RenderAsync("""{% image light: "a-light.png", dark: "a-dark.png", alt: "A", theme: "attribute" %}""");

        Assert.Equal(
            "<img src=\"/assets/images/a-light-40.HASH.png\" width=\"40\" height=\"20\" alt=\"A\" loading=\"lazy\" decoding=\"async\" data-theme=\"light\">"
            + "<img src=\"/assets/images/a-dark-40.HASH.png\" width=\"40\" height=\"20\" alt=\"A\" loading=\"lazy\" decoding=\"async\" data-theme=\"dark\">",
            Normalize(html));
    }

    [Fact]
    public async Task DefaultsFromTheMarkdownSection()
    {
        Site = await SiteOptions.DetectAsync(Source, Output, null, null, markdown: new MarkdownOptions
        {
            Images = new ImageOptions([50, 100], ["webp"], 60, "media/img", "eager"),
        });

        await Source.WriteBinaryFileAsync("/_content/docs/a.jpg", TestImages.Solid(200, 100, (200, 0, 0), ".jpg"));

        var html = await RenderAsync("---\ntitle: Red\n---\n{% image \"a.jpg\", alt: title %}");

        Assert.Equal("<img src=\"/media/img/a-100.HASH.webp\" srcset=\"/media/img/a-50.HASH.webp 50w, /media/img/a-100.HASH.webp 100w\" width=\"100\" height=\"50\" alt=\"Red\" loading=\"eager\" decoding=\"async\">", Normalize(html));
    }

    [Fact]
    public async Task VariablesAsArguments()
    {
        await Source.WriteBinaryFileAsync("/_content/docs/a.png", TestImages.Solid(200, 100, (0, 0, 0)));

        var html = await RenderAsync("---\nhero: a.png\nlabel: Hero\nsizes: [50]\n---\n{% image hero, alt: label, widths: sizes %}");

        Assert.Equal("<img src=\"/assets/images/a-50.HASH.png\" width=\"50\" height=\"25\" alt=\"Hero\" loading=\"lazy\" decoding=\"async\">", Normalize(html));
    }

    [Fact]
    public async Task TwoPagesUsingOneImageWriteOneFile()
    {
        await Source.WriteBinaryFileAsync("/_content/images/a.png", TestImages.Solid(200, 100, (0, 0, 0)));
        await Source.WriteTextFileAsync("/_content/one.html", """{% image "/images/a.png", widths: [100], formats: ["webp"] %}""");
        await Source.WriteTextFileAsync("/_content/blog/two.html", """{% image "../images/a.png", widths: [100], formats: ["webp"] %}""");

        var result = await BuildCachedAsync(cache: false);

        var variant = Assert.Single(Output.EnumerateFiles("/assets/images/"));
        Assert.Equal(2, result["write"].Results.Count(x => x.Key.Unit == variant));
        Assert.Equal(await Output.ReadAllTextAsync("/one/index.html"), await Output.ReadAllTextAsync("/blog/two/index.html"));
    }

    [Fact]
    public async Task RelativeSourceFromThePageFolder()
    {
        await Source.WriteBinaryFileAsync("/_content/images/a.png", TestImages.Solid(20, 10, (0, 0, 0)));

        var html = await RenderAsync("""{% image "../../images/a.png" %}""", "/_content/docs/guide/page.html");

        Assert.Equal("<img src=\"/assets/images/a-20.HASH.png\" width=\"20\" height=\"10\" alt=\"\" loading=\"lazy\" decoding=\"async\">", Normalize(html));
    }

    [Fact]
    public async Task SourceIsATemplateDependency()
    {
        await Source.WriteBinaryFileAsync("/_content/a.png", TestImages.Solid(20, 10, (0, 0, 0)));
        await Source.WriteTextFileAsync("/_content/page.html", """{% image "a.png" %}""");

        var result = await BuildCachedAsync(cache: false);

        var unit = result["render"].Results.Single(x => x.Key.Unit == "/page/index.html");
        Assert.Contains(unit.Dependencies.Templates, x => x.Path == "/_content/a.png");
        Assert.Contains(unit.Files, x => x.Path.FullName.StartsWith("/assets/images/a-20.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SecondBuildProcessesNothingAndASourceEditRenamesTheVariant()
    {
        await Source.WriteBinaryFileAsync("/_content/a.png", TestImages.Solid(200, 100, (0, 0, 0)));
        await Source.WriteTextFileAsync("/_content/page.html", """{% image "a.png", widths: [100] %}""");
        await Source.WriteTextFileAsync("/_content/other.html", "other");

        await BuildCachedAsync();
        var first = Output.EnumerateFiles("/assets/images/").Single();

        // Nothing changed: every unit is replayed, the variant with them.
        var second = await BuildCachedAsync();
        Assert.Equal(0, second.UnitsExecuted);
        Assert.Contains(second.Outputs, x => x.Path == first);

        // The image changed: the page renders again, with a variant of another name.
        await Source.WriteBinaryFileAsync("/_content/a.png", TestImages.Solid(200, 100, (255, 0, 0)));
        var third = await BuildCachedAsync();

        Assert.Equal(["/page/index.html"], Executed(third, "render"));
        var renamed = third.Outputs.Single(x => x.Path.FullName.StartsWith("/assets/images/", StringComparison.Ordinal)).Path;
        Assert.NotEqual(first, renamed.FullName);
        Assert.Contains(renamed.FullName, await Output.ReadAllTextAsync("/page/index.html"));
    }

    [Fact]
    public async Task ImageUrlAndImageSize()
    {
        await Source.WriteBinaryFileAsync("/_content/docs/a.png", TestImages.Solid(400, 300, (0, 0, 0)));
        await Source.WriteTextFileAsync("/_content/docs/b.svg", "<svg width=\"12\" height=\"8\"></svg>");

        var html = await RenderAsync("""{{ "a.png" | image_url: width: 200, format: "webp" }} {% assign s = "a.png" | image_size %}{{ s.width }}x{{ s.height }} {{ "b.svg" | image_url }} {% assign t = "b.svg" | image_size %}{{ t.width }}x{{ t.height }}""");

        Assert.Equal("/assets/images/a-200.HASH.webp 400x300 /assets/images/b.HASH.svg 12x8", Normalize(html));
        Assert.Equal(2, Output.EnumerateFiles("/assets/images/").Count());
    }

    [Fact]
    public async Task MissingImage()
    {
        await Source.WriteTextFileAsync("/_content/page.html", """{% image "nope.png" %}""");

        var result = await BuildAsync(options: Options(cache: false));

        var error = Assert.Single(result.Errors);
        Assert.Equal("image 'nope.png' not found ('/_content/nope.png')", error.Message);
    }

    [Fact]
    public async Task MissingLibvipsFailsOnlyTheUnitsThatNeedIt()
    {
        // A PNG needs libvips; an SVG and a page without images do not.
        await Source.WriteBinaryFileAsync("/_content/a.png", [0x89, (byte)'P', (byte)'N', (byte)'G', 0, 0, 0, 0]);
        await Source.WriteTextFileAsync("/_content/b.svg", "<svg width=\"1\" height=\"1\"></svg>");
        await Source.WriteTextFileAsync("/_content/png.html", """{% image "a.png" %}""");
        await Source.WriteTextFileAsync("/_content/svg.html", """{% image "b.svg" %}""");
        await Source.WriteTextFileAsync("/_content/plain.html", "plain");

        var options = Options(cache: false);
        var missing = new DllNotFoundException("Unable to load shared library 'libvips.42.dylib'");

        var result = await BuildAsync(options: new BuildOptions
        {
            MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
            KeepFiles = true,
            UseCache = false,
            Trace = options.Trace,
            View = options.View,
            Cancellation = Ct,
            Failure = FailurePolicy.KeepGoing,
            Images = _ => throw new DouzeException(ImageProcessor.MissingLibraryMessage(missing)),
        });

        var error = Assert.Single(result.Errors);
        Assert.Equal("/png/index.html", error.File?.FullName);
        Assert.StartsWith("image processing needs libvips, which cannot be loaded (Unable to load shared library 'libvips.42.dylib'). The douze tool packages of win-x64", error.Message);
        Assert.True(await Output.GetFileEntryAsync("/svg/index.html") is { Exists: true });
        Assert.True(await Output.GetFileEntryAsync("/plain/index.html") is { Exists: true });
    }

    [Fact]
    public async Task InvalidArguments()
    {
        await Source.WriteBinaryFileAsync("/_content/a.png", TestImages.Solid(20, 10, (0, 0, 0)));
        await Source.WriteTextFileAsync("/_content/page.html", """{% image "a.png", formats: ["bmp"] %}""");

        var result = await BuildAsync(options: Options(cache: false));

        Assert.Equal("unknown image format 'bmp'; expected 'webp', 'avif', 'png' or 'jpeg'", Assert.Single(result.Errors).Message);
    }
}
