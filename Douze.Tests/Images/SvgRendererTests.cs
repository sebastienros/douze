using Douze.Images;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Douze.Tests.Images;

public class SvgRendererTests
{
    private static Image<Rgba32> Render(string body, int width = 100, int height = 100, string attributes = "width=\"100\" height=\"100\"")
    {
        return SvgRenderer.Render($"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" {attributes}>{body}</svg>", width, height);
    }

    private static void Near(Rgba32 expected, Rgba32 actual, int tolerance = 3)
    {
        Assert.True(
            Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance && Math.Abs(expected.B - actual.B) <= tolerance && Math.Abs(expected.A - actual.A) <= tolerance,
            $"expected {expected}, got {actual}");
    }

    [Fact]
    public void FillsShapes()
    {
        using var image = Render("<rect x=\"10\" y=\"10\" width=\"30\" height=\"30\" fill=\"red\"/><circle cx=\"70\" cy=\"70\" r=\"20\" fill=\"#00ff00\"/><ellipse cx=\"75\" cy=\"20\" rx=\"20\" ry=\"10\" fill=\"rgb(0, 0, 255)\"/>");

        Near(new Rgba32(255, 0, 0, 255), image[25, 25]);
        Near(new Rgba32(0, 255, 0, 255), image[70, 70]);
        Near(new Rgba32(0, 0, 255, 255), image[75, 20]);
        Assert.Equal(new Rgba32(0, 0, 0, 0), image[5, 5]);
        Assert.Equal(new Rgba32(0, 0, 0, 0), image[52, 52]);
    }

    [Fact]
    public void BlackIsTheDefaultFill()
    {
        using var image = Render("<polygon points=\"0,0 100,0 100,100\"/>");

        Near(new Rgba32(0, 0, 0, 255), image[90, 10]);
        Assert.Equal(0, image[10, 90].A);
    }

    [Fact]
    public void StretchesTheViewBoxToTheSize()
    {
        // The intrinsic size (200 by 100) is stretched to 100 by 100; the view box maps 10 by 10 units on it.
        using var image = Render("<rect width=\"5\" height=\"10\" fill=\"#f00\"/><rect x=\"5\" width=\"5\" height=\"10\" fill=\"#00f\"/>", 100, 100, "width=\"200\" height=\"100\" viewBox=\"0 0 10 10\" preserveAspectRatio=\"none\"");

        Near(new Rgba32(255, 0, 0, 255), image[25, 50]);
        Near(new Rgba32(0, 0, 255, 255), image[75, 50]);
    }

    [Fact]
    public void FitsTheViewBoxByItsAspectRatio()
    {
        // xMidYMid meet: a square view box in a 200 by 100 viewport is centered, from x 50 to 150 (25 to 75 once stretched).
        using var image = Render("<rect width=\"10\" height=\"10\" fill=\"#f00\"/>", 100, 100, "width=\"200\" height=\"100\" viewBox=\"0 0 10 10\"");

        Assert.Equal(0, image[10, 50].A);
        Near(new Rgba32(255, 0, 0, 255), image[50, 50]);
        Assert.Equal(0, image[90, 50].A);
    }

    [Fact]
    public void AppliesTransforms()
    {
        using var image = Render("<g transform=\"translate(50, 0)\"><rect width=\"10\" height=\"10\" fill=\"red\" transform=\"scale(2)\"/></g><rect x=\"-5\" y=\"-5\" width=\"10\" height=\"10\" fill=\"blue\" transform=\"translate(20 80) rotate(45)\"/>");

        Near(new Rgba32(255, 0, 0, 255), image[65, 15]);
        Assert.Equal(0, image[45, 15].A);

        // A diamond: its center and the middle of its edges' corners, not the corners of the unrotated square.
        Near(new Rgba32(0, 0, 255, 255), image[20, 80]);
        Near(new Rgba32(0, 0, 255, 255), image[24, 80]);
        Assert.Equal(0, image[25, 75].A);
    }

    [Fact]
    public void PathsAndFillRules()
    {
        // Two nested squares in one path: even-odd makes a hole, non-zero (same direction) does not.
        const string data = "M10 10 H90 V90 H10 Z M30 30 H70 V70 H30 Z";
        using var evenOdd = Render($"<path d=\"{data}\" fill-rule=\"evenodd\" fill=\"#000\"/>");
        using var nonZero = Render($"<path d=\"{data}\" fill=\"#000\"/>");

        Assert.Equal(0, evenOdd[50, 50].A);
        Assert.Equal(255, evenOdd[20, 20].A);
        Assert.Equal(255, nonZero[50, 50].A);
    }

    [Fact]
    public void StrokesInUserSpace()
    {
        // A 4 unit stroke scaled by 2 is 8 pixels wide.
        using var image = Render("<line x1=\"0\" y1=\"25\" x2=\"50\" y2=\"25\" stroke=\"#0f0\" stroke-width=\"4\" transform=\"scale(2)\"/><rect x=\"10\" y=\"70\" width=\"80\" height=\"20\" fill=\"none\" stroke=\"blue\" stroke-width=\"2\"/>");

        Near(new Rgba32(0, 255, 0, 255), image[50, 47]);
        Near(new Rgba32(0, 255, 0, 255), image[50, 53]);
        Assert.Equal(0, image[50, 56].A);
        Near(new Rgba32(0, 0, 255, 255), image[50, 70]);
        Assert.Equal(0, image[50, 80].A);
    }

    [Fact]
    public void LinearGradientInTheBoundingBox()
    {
        using var image = Render("<defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"#000\"/><stop offset=\"1\" stop-color=\"#fff\"/></linearGradient></defs><rect x=\"50\" width=\"50\" height=\"100\" fill=\"url(#g)\" transform=\"translate(-50 0)\"/>");

        Assert.InRange(image[2, 50].R, 0, 20);
        Assert.InRange(image[25, 50].R, 110, 145);
        Assert.InRange(image[48, 50].R, 235, 255);
        Assert.Equal(0, image[75, 50].A);
    }

    [Fact]
    public void LinearGradientInUserSpaceWithInheritance()
    {
        using var image = Render("<linearGradient id=\"base\"><stop offset=\"0%\" style=\"stop-color: red\"/><stop offset=\"100%\" stop-color=\"blue\"/></linearGradient><linearGradient id=\"g\" href=\"#base\" gradientUnits=\"userSpaceOnUse\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"100\"/><rect width=\"100\" height=\"100\" fill=\"url(#g)\"/>");

        Assert.True(image[50, 2].R > 240 && image[50, 2].B < 15, $"top is {image[50, 2]}");
        Assert.True(image[50, 97].B > 240 && image[50, 97].R < 15, $"bottom is {image[50, 97]}");
        Assert.Equal(image[10, 50], image[90, 50]);
    }

    [Fact]
    public void RadialGradient()
    {
        using var image = Render("<radialGradient id=\"g\"><stop offset=\"0\" stop-color=\"#fff\"/><stop offset=\"1\" stop-color=\"#000\" stop-opacity=\"0.5\"/></radialGradient><rect width=\"100\" height=\"100\" fill=\"url(#g)\"/>");

        Assert.True(image[50, 50].R > 240, $"center is {image[50, 50]}");
        Assert.InRange(image[50, 1].A, 120, 135);
        Assert.InRange(image[1, 1].A, 120, 135);
    }

    [Fact]
    public void GroupOpacityComposesOnce()
    {
        // Two overlapping opaque squares in a group at 50%: the overlap is as transparent as the rest of the group.
        using var image = Render("<g opacity=\"0.5\"><rect width=\"60\" height=\"60\" fill=\"#f00\"/><rect x=\"40\" y=\"40\" width=\"60\" height=\"60\" fill=\"#f00\"/></g><rect y=\"90\" width=\"10\" height=\"10\" fill=\"#00f\" fill-opacity=\"0.25\"/>");

        Assert.InRange(image[20, 20].A, 120, 135);
        Assert.InRange(image[50, 50].A, 120, 135);
        Assert.InRange(image[5, 95].A, 58, 70);
    }

    [Fact]
    public void StyleSheetsAndStyleAttributes()
    {
        using var image = Render("<style>rect { fill: blue } .warm { fill: #f00 } #green { fill: lime }</style><rect width=\"50\" height=\"50\"/><rect x=\"50\" width=\"50\" height=\"50\" class=\"warm\"/><rect y=\"50\" width=\"50\" height=\"50\" class=\"warm\" id=\"green\"/><rect x=\"50\" y=\"50\" width=\"50\" height=\"50\" class=\"warm\" fill=\"black\" style=\"fill: yellow\"/>");

        Near(new Rgba32(0, 0, 255, 255), image[25, 25]);
        Near(new Rgba32(255, 0, 0, 255), image[75, 25]);
        Near(new Rgba32(0, 255, 0, 255), image[25, 75]);
        Near(new Rgba32(255, 255, 0, 255), image[75, 75]);
    }

    [Fact]
    public void InheritsAndUsesCurrentColor()
    {
        using var image = Render("<g fill=\"red\" color=\"#00f\"><rect width=\"50\" height=\"50\"/><rect x=\"50\" width=\"50\" height=\"50\" fill=\"currentColor\"/></g><g fill=\"red\" display=\"none\"><rect y=\"50\" width=\"50\" height=\"50\"/></g>");

        Near(new Rgba32(255, 0, 0, 255), image[25, 25]);
        Near(new Rgba32(0, 0, 255, 255), image[75, 25]);
        Assert.Equal(0, image[25, 75].A);
    }

    [Fact]
    public void UsesElementsAndSymbols()
    {
        using var image = Render("<defs><rect id=\"r\" width=\"20\" height=\"20\" fill=\"red\"/><symbol id=\"s\" viewBox=\"0 0 1 1\"><rect width=\"1\" height=\"1\" fill=\"blue\"/></symbol></defs><use href=\"#r\" x=\"10\" y=\"10\"/><use xlink:href=\"#s\" x=\"60\" y=\"60\" width=\"30\" height=\"30\"/>");

        Near(new Rgba32(255, 0, 0, 255), image[20, 20]);
        Assert.Equal(0, image[5, 5].A);
        Near(new Rgba32(0, 0, 255, 255), image[75, 75]);
        Assert.Equal(0, image[95, 95].A);
    }

    [Fact]
    public void DrawsInlinedImages()
    {
        var png = Convert.ToBase64String(TestImages.Solid(4, 4, (255, 0, 0)));
        var svg = Convert.ToBase64String("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"2\" height=\"2\"><rect width=\"2\" height=\"2\" fill=\"#00f\"/></svg>"u8.ToArray());

        using var image = Render($"<image href=\"data:image/png;base64,{png}\" x=\"0\" y=\"0\" width=\"40\" height=\"40\"/><image xlink:href=\"data:image/svg+xml;base64,{svg}\" x=\"50\" y=\"50\" width=\"40\" height=\"20\"/>");

        Near(new Rgba32(255, 0, 0, 255), image[20, 20]);
        Assert.Equal(0, image[45, 20].A);

        // A square image in a 40 by 20 box is centered (meet): from x 60 to 80.
        Near(new Rgba32(0, 0, 255, 255), image[70, 60]);
        Assert.Equal(0, image[55, 60].A);
    }

    [Fact]
    public void IgnoresForeignElements()
    {
        using var image = SvgRenderer.Render("<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\" width=\"10\" height=\"10\"><inkscape:grid/><title>t</title><rect width=\"10\" height=\"10\" fill=\"red\"/></svg>", 10, 10);

        Near(new Rgba32(255, 0, 0, 255), image[5, 5]);
    }

    [Theory]
    [InlineData("<text x=\"0\" y=\"10\">Hello</text>", "SVG <text> (line 1): <text> is not supported; lay out text with the 'text' blocks of the step")]
    [InlineData("<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>", "SVG <rect> (line 1): the property 'filter' is not supported")]
    [InlineData("<rect width=\"10\" height=\"10\" style=\"clip-path: url(#c)\"/>", "SVG <rect> (line 1): the property 'clip-path' is not supported")]
    [InlineData("<pattern id=\"p\"/><rect width=\"10\" height=\"10\" fill=\"url(#p)\"/>", "SVG <rect> (line 1): a paint of <pattern> is not supported")]
    [InlineData("<image href=\"https://example.com/a.png\" width=\"10\" height=\"10\"/>", "SVG <image> (line 1): the image 'https://example.com/a.png' is not inlined; reference images of the site by a relative path")]
    [InlineData("<rect width=\"10\" height=\"10\" fill=\"bluish\"/>", "SVG <rect> (line 1): invalid color 'bluish'")]
    [InlineData("<rect width=\"ten\" height=\"10\"/>", "SVG <rect> (line 1): invalid length 'ten' of 'width'")]
    [InlineData("<foreignObject/>", "SVG <foreignObject> (line 1): <foreignObject> is not supported")]
    [InlineData("<style>g rect { fill: red }</style>", "SVG <style> (line 1): the CSS selector 'g rect' is not supported; use a type, class or id selector")]
    public void ReportsWhatItDoesNotRender(string body, string message)
    {
        Assert.Equal(message, Assert.Throws<DouzeException>(() => Render(body)).Message);
    }

    [Fact]
    public void InvalidDocuments()
    {
        Assert.StartsWith("invalid SVG: ", Assert.Throws<DouzeException>(() => SvgRenderer.Render("<svg><rect></svg>", 10, 10)).Message);
        Assert.Equal("invalid SVG: the root element is not <svg>", Assert.Throws<DouzeException>(() => SvgRenderer.Render("<html/>", 10, 10)).Message);
        Assert.Equal("invalid SVG: the <svg> element has neither a width and a height nor a viewBox", Assert.Throws<DouzeException>(() => SvgRenderer.Render("<svg xmlns=\"http://www.w3.org/2000/svg\"/>", 10, 10)).Message);
    }

    [Theory]
    [InlineData("#abc", 170, 187, 204, 255)]
    [InlineData("rgba(255, 0, 0, 0.5)", 255, 0, 0, 128)]
    [InlineData("rgb(100% 0% 0%)", 255, 0, 0, 255)]
    [InlineData("hsl(120, 100%, 50%)", 0, 255, 0, 255)]
    [InlineData("rebeccapurple", 102, 51, 153, 255)]
    public void ParsesColors(string text, int r, int g, int b, int a)
    {
        Assert.True(SvgRenderer.TryParseColor(text, out var color));
        Assert.Equal(new Rgba32((byte)r, (byte)g, (byte)b, (byte)a), color.ToPixel<Rgba32>());
    }
}
