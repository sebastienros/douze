using System.Text;
using Douze.FileSystems;
using Douze.Images;
using Xunit;

namespace Douze.Tests.Images;

public class ImageFormatsTests
{
    [Theory]
    [InlineData("<svg viewBox=\"0 0 320 180\"></svg>", 320, 180)]
    [InlineData("<svg width=\"100\" height=\"50px\" viewBox=\"0 0 10 10\"></svg>", 100, 50)]
    [InlineData("<svg width=\"100%\" height=\"100%\" viewBox=\"0,0,64.4,32.2\"></svg>", 64, 32)]
    [InlineData("<svg width=\"200\" viewBox=\"0 0 400 100\"></svg>", 200, 50)]
    [InlineData("<svg height='30pt' viewBox='0 0 40 30'></svg>", 53, 40)]
    [InlineData("<?xml version=\"1.0\"?><!DOCTYPE svg><!-- <svg width=\"1\" height=\"1\"> --><svg\n  xmlns=\"http://www.w3.org/2000/svg\"\n  stroke-width=\"3\" width=\"24\" height=\"12\"></svg>", 24, 12)]
    public void SvgSize(string svg, int width, int height)
    {
        Assert.Equal((width, height), ImageFormats.ReadSize(ImageFormats.Svg, Encoding.UTF8.GetBytes(svg)));
    }

    [Fact]
    public void SvgWithoutSize()
    {
        var error = Assert.Throws<DouzeException>(() => ImageFormats.ReadSize(ImageFormats.Svg, "<svg width=\"50%\"></svg>"u8));

        Assert.Equal("the <svg> element has neither a width and a height nor a viewBox", error.Message);
    }

    [Fact]
    public void GifSizeFromItsHeader()
    {
        byte[] gif = [.. "GIF89a"u8, 0x40, 0x01, 0xC8, 0x00, 0, 0, 0];

        Assert.Equal(ImageFormats.Gif, ImageFormats.Sniff(gif));
        Assert.Equal((320, 200), ImageFormats.ReadSize(ImageFormats.Gif, gif));
    }

    [Fact]
    public void SniffAndNames()
    {
        Assert.Equal(ImageFormats.Png, ImageFormats.Sniff([0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10]));
        Assert.Equal(ImageFormats.Jpeg, ImageFormats.Sniff([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal(ImageFormats.Webp, ImageFormats.Sniff([.. "RIFF"u8, 0, 0, 0, 0, .. "WEBPVP8 "u8]));
        Assert.Null(ImageFormats.Sniff("hello"u8));

        Assert.Equal(ImageFormats.Jpeg, ImageFormats.Encodable("JPG"));
        Assert.Null(ImageFormats.Encodable("gif"));
        Assert.Equal(".webp[Q=75,keep=none]", ImageFormats.SaveOptions(ImageFormats.Webp, 75));
        Assert.Equal(".png[keep=none]", ImageFormats.SaveOptions(ImageFormats.Png, 75));
    }

    [Fact]
    public void FileNames()
    {
        var hash = Douze.Workflow.Hash128.Parse("0123456789abcdef0123456789abcdef");

        Assert.Equal("dashboard-640.01234567.webp", ImageVariant.FileName("dashboard", 640, hash, ImageFormats.Webp));
        Assert.Equal("arch.01234567.svg", ImageVariant.OriginalFileName("arch", hash, ".SVG"));
    }

    [Theory]
    [InlineData("images/a.png", "/_content/docs/", "/_content/docs/images/a.png")]
    [InlineData("../a.png?v=1#top", "/_content/docs/", "/_content/a.png")]
    [InlineData("/images/a%20b.png", "/_content/docs/", "/_content/images/a b.png")]
    [InlineData("./a.png", "/", "/a.png")]
    [InlineData("../../a.png", "/docs/", null)]
    [InlineData("https://example.com/a.png", "/", null)]
    [InlineData("data:image/png;base64,AAAA", "/", null)]
    [InlineData("//cdn/a.png", "/", null)]
    public void SourcePaths(string src, string directory, string? expected)
    {
        Assert.Equal(expected, ImageSources.Resolve(src, new FPath(directory), "/_content/")?.FullName);
    }
}
