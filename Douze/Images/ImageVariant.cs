using System.Globalization;
using Douze.Workflow;

namespace Douze.Images;

/// <summary>
/// A requested output of an image: its width (null for the width of the source; never wider than the source), its
/// format (a name of <see cref="ImageFormats.Encodable"/>) and the quality of lossy formats.
/// </summary>
internal readonly record struct ImageVariant(int? Width, string Format, int Quality)
{
    /// <summary>
    /// The file name of a variant: <c>&lt;name&gt;-&lt;width&gt;.&lt;hash8&gt;.&lt;ext&gt;</c>, the hash being the content
    /// hash of the variant, so that a changed source gets a new name.
    /// </summary>
    public static string FileName(string name, int width, Hash128 contentHash, ImageFormat format)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{name}-{width}.{Hash8(contentHash)}{format.Extension}");
    }

    /// <summary>
    /// The file name of a source copied as it is (an SVG, a GIF, the original of the <c>images</c> step):
    /// <c>&lt;name&gt;.&lt;hash8&gt;.&lt;ext&gt;</c>.
    /// </summary>
    public static string OriginalFileName(string name, Hash128 contentHash, string extension)
    {
        return $"{name}.{Hash8(contentHash)}{extension.ToLowerInvariant()}";
    }

    private static string Hash8(Hash128 hash) => hash.ToString()[..8];
}

/// <summary>
/// An encoded image and its size.
/// </summary>
internal sealed record ProcessedImage(ReadOnlyMemory<byte> Bytes, int Width, int Height);

/// <summary>
/// Decoded pixels: sRGB with an alpha channel, 8 bits per band, row by row, and their resolution in pixels per
/// millimeter (the <c>pHYs</c> of a PNG made from them).
/// </summary>
internal sealed record Raster(byte[] Pixels, int Width, int Height, double XResolution, double YResolution);

/// <summary>
/// The size and format of a source image, as displayed (EXIF orientation applied).
/// </summary>
internal readonly record struct ImageInfo(int Width, int Height, string Format, bool HasAlpha);

/// <summary>
/// A line of text of a social image, rendered with Pango and composited at <see cref="X"/>, <see cref="Y"/>: wrapped
/// at <see cref="Width"/>, clipped at <see cref="Height"/>, aligned in its box (<c>left</c>, <c>center</c>,
/// <c>right</c>). <see cref="FontFile"/> is a font file on disk that Pango loads before resolving <see cref="Font"/>.
/// </summary>
internal sealed record TextBlock(string Text, string Font, string? FontFile, int X, int Y, int Width, int Height, string Color, string Align);
