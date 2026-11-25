using Douze.Images;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace Douze.Tests.Images;

/// <summary>
/// Images made with ImageSharp for the image tests.
/// </summary>
internal static class TestImages
{
    /// <summary>
    /// A plain image of one color, encoded as <c>.png</c>, <c>.jpg</c> (quality 90) or <c>.webp</c>.
    /// </summary>
    public static byte[] Solid(int width, int height, (int R, int G, int B) color, string format = ".png")
    {
        using var image = new Image<Rgb24>(width, height, new Rgb24((byte)color.R, (byte)color.G, (byte)color.B));
        return Encode(image, format);
    }

    /// <summary>
    /// An image encoded as <c>.png</c>, <c>.jpg</c> (quality 90) or <c>.webp</c>, with its metadata.
    /// </summary>
    public static byte[] Encode(Image image, string format)
    {
        using var output = new MemoryStream();

        image.Save(output, format switch
        {
            // The default of the PNG encoder adds an alpha channel to an RGB image.
            ".png" => new PngEncoder { ColorType = image.PixelType.AlphaRepresentation == PixelAlphaRepresentation.None ? PngColorType.Rgb : PngColorType.RgbWithAlpha },
            ".jpg" => new JpegEncoder { Quality = 90 },
            ".webp" => new WebpEncoder(),
            _ => throw new ArgumentException($"unknown format '{format}'", nameof(format)),
        });

        return output.ToArray();
    }

    /// <summary>
    /// The size, format and channels (3, or 4 with alpha) of encoded image bytes.
    /// </summary>
    public static (int Width, int Height, string Format, int Channels) Describe(ReadOnlyMemory<byte> bytes)
    {
        var info = Image.Identify(bytes.Span);
        var format = ImageFormats.Sniff(bytes.Span)?.Name ?? "unknown";
        return (info.Width, info.Height, format, info.PixelType.AlphaRepresentation == PixelAlphaRepresentation.None ? 3 : 4);
    }

    /// <summary>
    /// The RGBA pixel of encoded image bytes at <paramref name="x"/>, <paramref name="y"/>.
    /// </summary>
    public static double[] Pixel(ReadOnlyMemory<byte> bytes, int x, int y)
    {
        using var image = Image.Load<Rgba32>(bytes.Span);
        var pixel = image[x, y];
        return [pixel.R, pixel.G, pixel.B, pixel.A];
    }

    /// <summary>
    /// The largest and the smallest value of a channel (0 to 3: red, green, blue, alpha) in a region of encoded image
    /// bytes.
    /// </summary>
    public static (int Max, int Min) Range(ReadOnlyMemory<byte> bytes, Rectangle region, int channel)
    {
        using var image = Image.Load<Rgba32>(bytes.Span);
        var (max, min) = (0, 255);

        for (var y = region.Top; y < region.Bottom; y++)
        {
            for (var x = region.Left; x < region.Right; x++)
            {
                var pixel = image[x, y];
                int value = channel switch
                {
                    0 => pixel.R,
                    1 => pixel.G,
                    2 => pixel.B,
                    _ => pixel.A,
                };

                (max, min) = (Math.Max(max, value), Math.Min(min, value));
            }
        }

        return (max, min);
    }

    /// <summary>
    /// The decoded pixels of encoded image bytes.
    /// </summary>
    public static Image<Rgba32> Decode(ReadOnlyMemory<byte> bytes) => Image.Load<Rgba32>(bytes.Span);

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
            "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
            @"C:\Windows\Fonts\arial.ttf",
        ];

        return candidates.FirstOrDefault(File.Exists);
    }
}
