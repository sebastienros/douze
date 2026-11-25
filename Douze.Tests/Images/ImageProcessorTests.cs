using Douze.Images;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.Metadata.Profiles.Xmp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;
using ImageInfo = Douze.Images.ImageInfo;

namespace Douze.Tests.Images;

public class ImageProcessorTests
{
    [Fact]
    public void VariantHasTheRequestedWidthAndFormat()
    {
        var images = ImageProcessor.Create();
        var source = TestImages.Solid(1600, 900, (10, 20, 30));

        var webp = images.Encode(source, new ImageVariant(400, "webp", 80));
        var png = images.Encode(source, new ImageVariant(400, "png", 80));
        var jpeg = images.Encode(source, new ImageVariant(null, "jpg", 80));

        Assert.Equal((400, 225), (webp.Width, webp.Height));
        Assert.Equal((400, 225, "webp", 3), TestImages.Describe(webp.Bytes));
        Assert.Equal((400, 225, "png", 3), TestImages.Describe(png.Bytes));
        Assert.Equal((1600, 900, "jpeg", 3), TestImages.Describe(jpeg.Bytes));
        Assert.Equal([10, 20, 30, 255], TestImages.Pixel(png.Bytes, 200, 100));
    }

    [Fact]
    public void VariantIsNeverWiderThanTheSource()
    {
        var images = ImageProcessor.Create();

        var variant = images.Encode(TestImages.Solid(300, 100, (0, 0, 0)), new ImageVariant(1200, "png", 0));

        Assert.Equal((300, 100), (variant.Width, variant.Height));
    }

    [Fact]
    public void VariantOfAJpegIsScaledOnLoad()
    {
        var images = ImageProcessor.Create();

        var variant = images.Encode(TestImages.Solid(2000, 1000, (200, 100, 50), ".jpg"), new ImageVariant(333, "webp", 80));

        Assert.Equal((333, 167, "webp", 3), TestImages.Describe(variant.Bytes));
    }

    [Fact]
    public void VariantKeepsTheAlpha()
    {
        var images = ImageProcessor.Create();
        using var transparent = new Image<Rgba32>(200, 100, new Rgba32(255, 0, 0, 128));
        var source = TestImages.Encode(transparent, ".png");

        var webp = images.Encode(source, new ImageVariant(100, "webp", 80));
        var png = images.Encode(source, new ImageVariant(100, "png", 80));
        var jpeg = images.Encode(source, new ImageVariant(100, "jpeg", 80));

        Assert.Equal((100, 50, "webp", 4), TestImages.Describe(webp.Bytes));
        Assert.Equal([255, 0, 0, 128], TestImages.Pixel(png.Bytes, 50, 25));

        // JPEG has no alpha: the image is flattened on white.
        Assert.Equal((100, 50, "jpeg", 3), TestImages.Describe(jpeg.Bytes));
        Assert.InRange(TestImages.Pixel(jpeg.Bytes, 50, 25)[1], 110, 145);
    }

    [Fact]
    public void VariantStripsMetadata()
    {
        var images = ImageProcessor.Create();

        using var tagged = new Image<Rgb24>(400, 300, new Rgb24(90, 90, 90));
        tagged.Metadata.XmpProfile = new XmpProfile(System.Text.Encoding.UTF8.GetBytes($"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">{new string('x', 2000)}</x:xmpmeta>"));
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Copyright, "someone");
        tagged.Metadata.ExifProfile = exif;
        var source = TestImages.Encode(tagged, ".jpg");

        var variant = images.Encode(source, new ImageVariant(200, "webp", 80));

        var info = Image.Identify(variant.Bytes.Span);
        Assert.Null(info.Metadata.XmpProfile);
        Assert.Null(info.Metadata.ExifProfile);
    }

    [Fact]
    public async Task VariantsAreMemoizedForTheBuild()
    {
        var images = ImageProcessor.Create();
        var source = TestImages.Solid(200, 100, (0, 0, 0));

        var first = images.VariantAsync(source, new ImageVariant(50, "webp", 80));
        var second = images.VariantAsync(source.ToArray(), new ImageVariant(50, "webp", 80));

        Assert.Same(first, second);
        Assert.NotSame(first, images.VariantAsync(source, new ImageVariant(50, "webp", 70)));
        Assert.Same(await first, await second);
    }

    [Fact]
    public void ProbeAppliesTheOrientation()
    {
        var images = ImageProcessor.Create();

        using var rotated = new Image<Rgb24>(600, 400, new Rgb24(0, 0, 0));
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)6);
        rotated.Metadata.ExifProfile = exif;
        var source = TestImages.Encode(rotated, ".jpg");

        Assert.Equal(new ImageInfo(400, 600, "jpeg", false), images.Probe(source));
        Assert.Equal((200, 300), Size(images.Encode(source, new ImageVariant(200, "png", 0))));

        static (int, int) Size(ProcessedImage image) => (image.Width, image.Height);
    }

    [Fact]
    public void ProbeReadsTheAlpha()
    {
        var images = ImageProcessor.Create();
        using var transparent = new Image<Rgba32>(30, 20);

        Assert.Equal(new ImageInfo(30, 20, "png", true), images.Probe(TestImages.Encode(transparent, ".png")));
        Assert.Equal(new ImageInfo(30, 20, "webp", false), images.Probe(TestImages.Solid(30, 20, (1, 2, 3), ".webp")));
    }

    [Fact]
    public void InvalidImage()
    {
        var images = ImageProcessor.Create();

        var error = Assert.Throws<DouzeException>(() => images.Probe("not an image"u8.ToArray()));
        Assert.Equal("not an image format douze reads (png, jpeg, webp, gif, svg)", error.Message);

        var truncated = TestImages.Solid(100, 100, (0, 0, 0)).AsMemory(0, 40);
        Assert.Throws<DouzeException>(() => images.Encode(truncated, new ImageVariant(10, "webp", 80)));
    }

    [Fact]
    public void AvifIsNotAFormat()
    {
        Assert.Null(ImageFormats.Encodable("avif"));
        Assert.Null(ImageFormats.FromExtension(".avif"));
        Assert.Equal("cannot encode images as 'avif'; expected 'webp', 'png' or 'jpeg'", Assert.Throws<DouzeException>(() => ImageProcessor.Create().Encode(TestImages.Solid(1, 1, (0, 0, 0)), new ImageVariant(1, "avif", 80))).Message);
    }

    [Fact]
    public void RasterizeAndCompose()
    {
        var images = ImageProcessor.Create();
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"120\" height=\"63\"><rect width=\"120\" height=\"63\" fill=\"#204080\"/></svg>";

        var background = images.Rasterize(svg, 1200, 630);
        Assert.Equal((1200, 630), (background.Width, background.Height));
        Assert.Equal(new Rgba32(32, 64, 128, 255), background.Image[600, 300]);
        Assert.Same(background, images.Rasterize(svg, 1200, 630));

        var font = TestImages.FontFile() is { } path ? new FontSource(File.ReadAllBytes(path), Douze.Workflow.Hash128.Of(File.ReadAllBytes(path))) : null;
        var blocks = new List<TextBlock>
        {
            new("Title & more", "sans bold 60", font, 80, 100, 1040, 200, "#ffffff", "left"),
            new("Centered", "sans 30", font, 0, 500, 1200, 100, "#ff0000", "center"),
        };

        var png = images.Compose(background, blocks);

        Assert.Equal((1200, 630, "png", 4), TestImages.Describe(png));

        // Outside the text, the background; inside the boxes, some text pixels.
        Assert.Equal([32, 64, 128, 255], TestImages.Pixel(png, 10, 10));
        Assert.True(TestImages.Range(png, new Rectangle(80, 100, 1040, 200), 0).Max > 200, "no white text in the title box");
        Assert.True(TestImages.Range(png, new Rectangle(0, 500, 1200, 100), 0).Max > 200, "no red text in the second box");

        // Centered: the text is in the middle of its box, not at its left.
        Assert.Equal(32, TestImages.Range(png, new Rectangle(0, 500, 400, 100), 0).Max);
        Assert.True(TestImages.Range(png, new Rectangle(500, 500, 200, 100), 0).Max > 200, "the second block is not centered");

        // The background raster is not changed by the text drawn over it.
        Assert.Equal(new Rgba32(32, 64, 128, 255), background.Image[600, 530]);
    }

    [Fact]
    public void TextIsClippedAtItsBox()
    {
        var images = ImageProcessor.Create();
        var background = images.Rasterize("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"400\" height=\"200\"><rect width=\"400\" height=\"200\" fill=\"#000\"/></svg>", 400, 200);
        var font = TestImages.FontFile() is { } path ? new FontSource(File.ReadAllBytes(path), Douze.Workflow.Hash128.Of(File.ReadAllBytes(path))) : null;

        var png = images.Compose(background, [new TextBlock("A long text that wraps over several lines", "sans 40", font, 0, 0, 200, 50, "#fff", "left")]);

        Assert.True(TestImages.Range(png, new Rectangle(0, 0, 200, 50), 0).Max > 200);
        Assert.Equal(0, TestImages.Range(png, new Rectangle(0, 50, 400, 150), 0).Max);
        Assert.Equal(0, TestImages.Range(png, new Rectangle(200, 0, 200, 200), 0).Max);
    }

    [Fact]
    public void ColorsAreValidated()
    {
        Assert.Equal((255, 0, 0, 255), ImageProcessor.ParseColor("#f00"));
        Assert.Equal((200, 200, 208, 128), ImageProcessor.ParseColor("#c8c8d080"));
        Assert.Equal("invalid color 'red'; expected #rrggbb or #rrggbbaa", Assert.Throws<DouzeException>(() => ImageProcessor.ParseColor("red")).Message);
    }

    [Fact]
    public void VersionIsImageSharps()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+", ImageProcessor.Create().Version);
    }
}
