using Douze.Images;
using NetVips;
using Xunit;

namespace Douze.Tests.Images;

/// <summary>
/// Images made with libvips for the image tests, which are skipped when libvips is not available.
/// </summary>
internal static class TestImages
{
    /// <summary>
    /// The processor, or skips the test when libvips cannot be loaded.
    /// </summary>
    public static ImageProcessor RequireLibvips()
    {
        try
        {
            return ImageProcessor.Create();
        }
        catch (DouzeException e)
        {
            Assert.Skip(e.Message);
            throw;
        }
    }

    /// <summary>
    /// A plain image of one color, encoded with libvips save options (<c>.png</c>, <c>.jpg[Q=90]</c>).
    /// </summary>
    public static byte[] Solid(int width, int height, (int R, int G, int B) color, string format = ".png")
    {
        RequireLibvips();

        using var black = Image.Black(width, height, bands: 3);
        using var filled = black + new double[] { color.R, color.G, color.B };
        using var image = filled.Cast(Enums.BandFormat.Uchar).Copy(interpretation: Enums.Interpretation.Srgb);
        return image.WriteToBuffer(format);
    }

    /// <summary>
    /// The size, format (loader) and bands of encoded image bytes.
    /// </summary>
    public static (int Width, int Height, string Loader, int Bands) Describe(ReadOnlyMemory<byte> bytes)
    {
        using var image = Image.NewFromBuffer(bytes.Span);
        return (image.Width, image.Height, (string)image.Get("vips-loader"), image.Bands);
    }

    /// <summary>
    /// The pixel of encoded image bytes at <paramref name="x"/>, <paramref name="y"/>.
    /// </summary>
    public static double[] Pixel(ReadOnlyMemory<byte> bytes, int x, int y)
    {
        using var image = Image.NewFromBuffer(bytes.Span);
        return image.Getpoint(x, y);
    }

    /// <summary>
    /// A font file of the system for the text tests, or null.
    /// </summary>
    public static string? FontFile()
    {
        string[] candidates =
        [
            "/System/Library/Fonts/Supplemental/Arial.ttf",
            "/Library/Fonts/Arial.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/dejavu/DejaVuSans.ttf",
            @"C:\Windows\Fonts\arial.ttf",
        ];

        return candidates.FirstOrDefault(File.Exists);
    }
}
