using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Douze.Workflow;

namespace Douze.Images;

/// <summary>
/// An image format: its name in options (<c>webp</c>), the extension and content type of its files, and whether
/// variants are encoded in it or files of it are copied as they are (SVG, GIF).
/// </summary>
internal sealed record ImageFormat(string Name, string Extension, string ContentType, bool CopiedAsIs, bool HasQuality);

/// <summary>
/// The formats of the image features: names, extensions and content types, the libvips save options of each, and
/// the sizes of the formats read without libvips (SVG from its attributes, GIF from its header).
/// </summary>
internal static class ImageFormats
{
    public static readonly ImageFormat Jpeg = new("jpeg", ".jpg", "image/jpeg", CopiedAsIs: false, HasQuality: true);
    public static readonly ImageFormat Png = new("png", ".png", "image/png", CopiedAsIs: false, HasQuality: false);
    public static readonly ImageFormat Webp = new("webp", ".webp", "image/webp", CopiedAsIs: false, HasQuality: true);
    public static readonly ImageFormat Avif = new("avif", ".avif", "image/avif", CopiedAsIs: false, HasQuality: true);
    public static readonly ImageFormat Gif = new("gif", ".gif", "image/gif", CopiedAsIs: true, HasQuality: false);
    public static readonly ImageFormat Svg = new("svg", ".svg", "image/svg+xml", CopiedAsIs: true, HasQuality: false);

    /// <summary>
    /// The quality of lossy variants when no option sets it.
    /// </summary>
    public const int DefaultQuality = 80;

    /// <summary>
    /// The image files the <c>images</c> step consumes by default.
    /// </summary>
    public static readonly GlobSet Sources = GlobSet.Parse(["**/*.{png,jpg,jpeg,webp,avif,gif,svg}"]);

    /// <summary>
    /// The names variants can be encoded in, for messages.
    /// </summary>
    public const string EncodableNames = "'webp', 'avif', 'png' or 'jpeg'";

    /// <summary>
    /// A format variants can be encoded in, by name (<c>jpg</c> is <c>jpeg</c>), or null.
    /// </summary>
    public static ImageFormat? Encodable(string name) => name.ToLowerInvariant() switch
    {
        "webp" => Webp,
        "avif" => Avif,
        "png" => Png,
        "jpeg" or "jpg" => Jpeg,
        _ => null,
    };

    /// <summary>
    /// The format of a source file by extension, or null for a file that is not an image the features read.
    /// </summary>
    public static ImageFormat? FromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => Png,
        ".jpg" or ".jpeg" => Jpeg,
        ".webp" => Webp,
        ".avif" => Avif,
        ".gif" => Gif,
        ".svg" => Svg,
        _ => null,
    };

    /// <summary>
    /// The format of image bytes from their signature, or null.
    /// </summary>
    public static ImageFormat? Sniff(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']))
        {
            return Png;
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return Jpeg;
        }

        if (bytes.StartsWith("GIF8"u8))
        {
            return Gif;
        }

        if (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return Webp;
        }

        if (bytes.Length >= 12 && bytes[4..8].SequenceEqual("ftyp"u8))
        {
            return Avif;
        }

        // SVG is text: its root element is found in the first kilobytes.
        var head = bytes[..Math.Min(bytes.Length, 4096)];
        return head.IndexOf("<svg"u8) >= 0 ? Svg : null;
    }

    /// <summary>
    /// The libvips save options of a variant: <c>.webp[Q=80,keep=none]</c>. Metadata (EXIF, XMP, ICC) is never kept.
    /// </summary>
    public static string SaveOptions(ImageFormat format, int quality)
    {
        return format.HasQuality
            ? string.Create(CultureInfo.InvariantCulture, $"{format.Extension}[Q={quality},keep=none]")
            : $"{format.Extension}[keep=none]";
    }

    /// <summary>
    /// The size of a GIF (its logical screen) or an SVG, read without decoding it: an SVG's from its <c>width</c> and
    /// <c>height</c> attributes, or its <c>viewBox</c> for those missing or relative.
    /// </summary>
    /// <exception cref="DouzeException">The size cannot be read.</exception>
    public static (int Width, int Height) ReadSize(ImageFormat format, ReadOnlySpan<byte> bytes)
    {
        if (format == Gif)
        {
            if (bytes.Length < 10)
            {
                throw new DouzeException("not a GIF file");
            }

            return (BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]));
        }

        if (format == Svg)
        {
            return SvgSize(Encoding.UTF8.GetString(bytes));
        }

        throw new InvalidOperationException($"the size of '{format.Name}' is read by libvips");
    }

    private static (int Width, int Height) SvgSize(string svg)
    {
        var start = FindRootElement(svg);

        if (start < 0)
        {
            throw new DouzeException("no <svg> element");
        }

        var end = svg.IndexOf('>', start);

        if (end < 0)
        {
            throw new DouzeException("the <svg> element is not closed");
        }

        var tag = svg.AsSpan(start, end - start);
        var width = Length(Attribute(tag, "width"));
        var height = Length(Attribute(tag, "height"));
        var viewBox = ViewBox(Attribute(tag, "viewBox"));

        if (width is { } w && height is { } h)
        {
            return (Round(w), Round(h));
        }

        if (viewBox is not var (boxWidth, boxHeight) || boxWidth <= 0 || boxHeight <= 0)
        {
            throw new DouzeException("the <svg> element has neither a width and a height nor a viewBox");
        }

        // One dimension given: the other keeps the aspect of the view box.
        return (width, height) switch
        {
            ({ } only, null) => (Round(only), Round(only * boxHeight / boxWidth)),
            (null, { } only) => (Round(only * boxWidth / boxHeight), Round(only)),
            _ => (Round(boxWidth), Round(boxHeight)),
        };

        static int Round(double value) => Math.Max(1, (int)Math.Round(value, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// The start of the <c>&lt;svg</c> tag, after the XML declaration, comments and a doctype.
    /// </summary>
    private static int FindRootElement(string svg)
    {
        var index = 0;

        while ((index = svg.IndexOf('<', index)) >= 0)
        {
            if (string.CompareOrdinal(svg, index, "<!--", 0, 4) == 0)
            {
                var close = svg.IndexOf("-->", index + 4, StringComparison.Ordinal);
                index = close < 0 ? svg.Length : close + 3;
                continue;
            }

            if (string.CompareOrdinal(svg, index, "<svg", 0, 4) == 0 && index + 4 < svg.Length && (char.IsWhiteSpace(svg[index + 4]) || svg[index + 4] is '>' or '/'))
            {
                return index;
            }

            index++;
        }

        return -1;
    }

    private static string? Attribute(ReadOnlySpan<char> tag, string name)
    {
        var index = 0;

        while (true)
        {
            var found = tag[index..].IndexOf(name, StringComparison.Ordinal);

            if (found < 0)
            {
                return null;
            }

            found += index;
            index = found + name.Length;

            // A whole attribute name: preceded by white space, followed by '='.
            if (!char.IsWhiteSpace(tag[found - 1]))
            {
                continue;
            }

            var rest = tag[index..].TrimStart();

            if (rest.IsEmpty || rest[0] != '=')
            {
                continue;
            }

            rest = rest[1..].TrimStart();

            if (rest.IsEmpty || rest[0] is not ('"' or '\''))
            {
                return null;
            }

            var close = rest[1..].IndexOf(rest[0]);
            return close < 0 ? null : rest.Slice(1, close).ToString();
        }
    }

    /// <summary>
    /// An absolute length in CSS pixels; null for a missing or relative one (percentages, <c>em</c>).
    /// </summary>
    private static double? Length(string? value)
    {
        if (value == null)
        {
            return null;
        }

        var text = value.Trim();
        var unitStart = text.Length;

        while (unitStart > 0 && char.IsAsciiLetter(text[unitStart - 1]))
        {
            unitStart--;
        }

        if (!double.TryParse(text.AsSpan(0, unitStart), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number <= 0)
        {
            return null;
        }

        return text[unitStart..] switch
        {
            "" or "px" => number,
            "pt" => number * 96 / 72,
            "pc" => number * 16,
            "in" => number * 96,
            "cm" => number * 96 / 2.54,
            "mm" => number * 96 / 25.4,
            _ => null,
        };
    }

    private static (double Width, double Height)? ViewBox(string? value)
    {
        if (value == null)
        {
            return null;
        }

        var parts = value.Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 4
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
            || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
        {
            return null;
        }

        return (width, height);
    }
}
