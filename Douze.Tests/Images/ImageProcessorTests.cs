using System.Text;
using Douze.Images;
using NetVips;
using Xunit;

namespace Douze.Tests.Images;

public class ImageProcessorTests
{
    [Fact]
    public void VariantHasTheRequestedWidthAndFormat()
    {
        var images = TestImages.RequireLibvips();
        var source = TestImages.Solid(1600, 900, (10, 20, 30));

        var webp = images.Variant(source, new ImageVariant(400, "webp", 80));
        var avif = images.Variant(source, new ImageVariant(400, "avif", 60));
        var jpeg = images.Variant(source, new ImageVariant(null, "jpg", 80));

        Assert.Equal((400, 225), (webp.Width, webp.Height));
        Assert.Equal((400, 225, "webpload_buffer", 3), TestImages.Describe(webp.Bytes));
        Assert.Equal(ImageFormats.Avif, ImageFormats.Sniff(avif.Bytes.Span));
        Assert.Equal((1600, 900, "jpegload_buffer", 3), TestImages.Describe(jpeg.Bytes));
    }

    [Fact]
    public void VariantIsNeverWiderThanTheSource()
    {
        var images = TestImages.RequireLibvips();

        var variant = images.Variant(TestImages.Solid(300, 100, (0, 0, 0)), new ImageVariant(1200, "png", 0));

        Assert.Equal((300, 100), (variant.Width, variant.Height));
    }

    [Fact]
    public void VariantStripsMetadata()
    {
        var images = TestImages.RequireLibvips();

        using var plain = Image.NewFromBuffer(TestImages.Solid(400, 300, (90, 90, 90), ".jpg"));
        using var tagged = plain.Mutate(x => x.Set(GValue.BlobType, "xmp-data", Encoding.UTF8.GetBytes(new string('x', 2000))));
        var source = tagged.WriteToBuffer(".jpg[Q=90]");

        var variant = images.Variant(source, new ImageVariant(200, "webp", 80));
        using var kept = Image.ThumbnailBuffer(source, 200);
        var keepAll = kept.WriteToBuffer(".webp[Q=80]");

        using var decoded = Image.NewFromBuffer(variant.Bytes.Span);
        Assert.DoesNotContain("xmp-data", decoded.GetFields());
        Assert.True(variant.Bytes.Length < keepAll.Length, $"{variant.Bytes.Length} >= {keepAll.Length}");
    }

    [Fact]
    public void VariantsAreMemoizedForTheBuild()
    {
        var images = TestImages.RequireLibvips();
        var source = TestImages.Solid(200, 100, (0, 0, 0));

        var first = images.Variant(source, new ImageVariant(50, "webp", 80));
        var second = images.Variant(source.ToArray(), new ImageVariant(50, "webp", 80));

        Assert.Same(first, second);
        Assert.NotSame(first, images.Variant(source, new ImageVariant(50, "webp", 70)));
    }

    [Fact]
    public void ProbeAppliesTheOrientation()
    {
        var images = TestImages.RequireLibvips();

        using var plain = Image.NewFromBuffer(TestImages.Solid(600, 400, (0, 0, 0), ".jpg"));
        using var rotated = plain.Mutate(x => x.Set(GValue.GIntType, "orientation", 6));
        var source = rotated.WriteToBuffer(".jpg");

        Assert.Equal(new ImageInfo(400, 600, "jpeg", false), images.Probe(source));
        Assert.Equal((200, 300), Size(images.Variant(source, new ImageVariant(200, "png", 0))));

        static (int, int) Size(ProcessedImage image) => (image.Width, image.Height);
    }

    [Fact]
    public void InvalidImage()
    {
        var images = TestImages.RequireLibvips();

        var error = Assert.Throws<DouzeException>(() => images.Probe("not an image"u8.ToArray()));

        Assert.Equal("not an image format douze reads (png, jpeg, webp, avif, gif, svg)", error.Message);
    }

    [Fact]
    public void RasterizeAndCompose()
    {
        var images = TestImages.RequireLibvips();
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"120\" height=\"63\"><rect width=\"120\" height=\"63\" fill=\"#204080\"/></svg>";

        var background = images.Rasterize(svg, 1200, 630);
        Assert.Equal((1200, 630, "pngload_buffer", 4), TestImages.Describe(background));
        Assert.Equal([32, 64, 128, 255], TestImages.Pixel(background, 600, 300));

        var font = TestImages.FontFile();
        var blocks = new List<TextBlock>
        {
            new("Title & more", "sans bold 60", font, 80, 100, 1040, 200, "#ffffff", "left"),
            new("Centered", "sans 30", font, 0, 500, 1200, 100, "#ff0000", "center"),
        };

        var png = images.Compose(background, blocks);

        Assert.Equal((1200, 630, "pngload_buffer", 4), TestImages.Describe(png));

        // Outside the text, the background; inside the boxes, some text pixels.
        Assert.Equal([32, 64, 128, 255], TestImages.Pixel(png, 10, 10));

        using var image = Image.NewFromBuffer(png.ToArray());
        using var title = image.Crop(80, 100, 1040, 200);
        using var centered = image.Crop(0, 500, 1200, 100);
        Assert.True(title.ExtractBand(0).Max() > 200, "no white text in the title box");
        Assert.True(centered.ExtractBand(0).Max() > 200 && centered.ExtractBand(1).Min() < 64, "no red text in the second box");
    }

    [Fact]
    public void ColorsAreValidated()
    {
        Assert.Equal((255, 0, 0, 255), ImageProcessor.ParseColor("#f00"));
        Assert.Equal((200, 200, 208, 128), ImageProcessor.ParseColor("#c8c8d080"));
        Assert.Equal("invalid color 'red'; expected #rrggbb or #rrggbbaa", Assert.Throws<DouzeException>(() => ImageProcessor.ParseColor("red")).Message);
    }

    [Fact]
    public void MissingLibraryMessageHasTheInstallHint()
    {
        var message = ImageProcessor.MissingLibraryMessage(new DllNotFoundException("Unable to load shared library 'libvips.so.42'\nmore"));

        Assert.Equal(
            "image processing needs libvips, which cannot be loaded (Unable to load shared library 'libvips.so.42'). The douze tool packages of win-x64, "
            + "win-arm64, linux-x64, linux-arm64, osx-x64 and osx-arm64 include it; the platform independent package does not: install douze on one of "
            + "these platforms, or put libvips 8.18 (libvips.so.42, libvips.42.dylib or libvips-42.dll) next to the douze executable or on the library path",
            message);
    }
}
