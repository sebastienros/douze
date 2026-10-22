using System.Globalization;
using System.Text;
using Douze.Markdown;

namespace Douze.Images;

/// <summary>
/// How a light and a dark image are told apart: <c>media</c> sources in one <c>&lt;picture&gt;</c>
/// (<c>prefers-color-scheme</c>), or two elements with a <c>data-theme</c> attribute for a site that toggles a theme
/// attribute and hides one of them in CSS (<c>theme: attribute</c>).
/// </summary>
internal enum ThemeMode { Media, Attribute }

/// <summary>
/// The markup options of an image: <c>alt</c>, <c>sizes</c>, <c>class</c>, <c>loading</c>, <c>decoding</c>, and the
/// other attributes of the <c>&lt;img&gt;</c>, whose values are HTML encoded already (null for an attribute without
/// a value).
/// </summary>
internal sealed record PictureOptions(string? Alt, string? Sizes, string? Class, string Loading, string Decoding, IReadOnlyList<(string Name, string? EncodedValue)> Attributes, ThemeMode Theme = ThemeMode.Media);

/// <summary>
/// The markup of an image: a <c>&lt;picture&gt;</c> with one <c>&lt;source&gt;</c> per format but the last, whose
/// <c>srcset</c> lists every width, and an <c>&lt;img&gt;</c> of the last format (the fallback) with its largest width,
/// <c>width</c> and <c>height</c>, so that the page does not move while it loads. A single format gives a plain
/// <c>&lt;img&gt;</c>, with a <c>srcset</c> when there are several widths.
/// </summary>
internal static class PictureMarkup
{
    private const string DarkMedia = "(prefers-color-scheme: dark)";

    public static string Write(ImageSet light, ImageSet? dark, PictureOptions options)
    {
        var builder = new StringBuilder();

        if (dark != null && options.Theme == ThemeMode.Attribute)
        {
            WriteOne(builder, light, null, options, "light");
            WriteOne(builder, dark, null, options, "dark");
        }
        else
        {
            WriteOne(builder, light, dark, options, null);
        }

        return builder.ToString();
    }

    private static void WriteOne(StringBuilder builder, ImageSet image, ImageSet? dark, PictureOptions options, string? theme)
    {
        var groups = Groups(image);
        var fallback = groups[^1];
        var picture = groups.Count > 1 || dark != null;

        if (picture)
        {
            builder.Append("<picture");
            AppendTheme(builder, theme);
            builder.Append('>');

            if (dark != null)
            {
                foreach (var group in Groups(dark))
                {
                    AppendSource(builder, group, options.Sizes, DarkMedia, withSize: true);
                }
            }

            foreach (var group in groups.Take(groups.Count - 1))
            {
                AppendSource(builder, group, options.Sizes, null, withSize: false);
            }
        }

        var largest = fallback[^1];

        builder.Append("<img src=\"").Append(Encode(largest.Url)).Append('"');

        if (fallback.Count > 1)
        {
            builder.Append(" srcset=\"").Append(Encode(SrcSet(fallback))).Append('"');

            if (options.Sizes != null)
            {
                builder.Append(" sizes=\"").Append(Encode(options.Sizes)).Append('"');
            }
        }

        AppendSize(builder, largest);
        builder.Append(" alt=\"").Append(Encode(options.Alt ?? "")).Append('"');

        if (!string.IsNullOrEmpty(options.Class))
        {
            builder.Append(" class=\"").Append(Encode(options.Class)).Append('"');
        }

        builder.Append(" loading=\"").Append(Encode(options.Loading)).Append('"');
        builder.Append(" decoding=\"").Append(Encode(options.Decoding)).Append('"');

        foreach (var (name, value) in options.Attributes)
        {
            builder.Append(' ').Append(name);

            if (value != null)
            {
                builder.Append("=\"").Append(value).Append('"');
            }
        }

        if (!picture)
        {
            AppendTheme(builder, theme);
        }

        builder.Append('>');

        if (picture)
        {
            builder.Append("</picture>");
        }
    }

    /// <summary>
    /// The variants by format, in the order of the set (formats as requested, widths ascending).
    /// </summary>
    private static List<List<ImageFile>> Groups(ImageSet image)
    {
        var groups = new List<List<ImageFile>>();

        foreach (var variant in image.Variants)
        {
            if (groups.Count == 0 || groups[^1][0].Format != variant.Format)
            {
                groups.Add([]);
            }

            groups[^1].Add(variant);
        }

        if (groups.Count == 0)
        {
            throw new DouzeException($"image '{image.Source}' has no variant");
        }

        return groups;
    }

    private static void AppendSource(StringBuilder builder, List<ImageFile> group, string? sizes, string? media, bool withSize)
    {
        builder.Append("<source");

        if (media != null)
        {
            builder.Append(" media=\"").Append(Encode(media)).Append('"');
        }

        if (ContentType(group[0].Format) is { } type)
        {
            builder.Append(" type=\"").Append(type).Append('"');
        }

        builder.Append(" srcset=\"").Append(Encode(SrcSet(group))).Append('"');

        if (sizes != null && group.Count > 1)
        {
            builder.Append(" sizes=\"").Append(Encode(sizes)).Append('"');
        }

        if (withSize)
        {
            AppendSize(builder, group[^1]);
        }

        builder.Append('>');
    }

    private static void AppendSize(StringBuilder builder, ImageFile file)
    {
        builder.Append(CultureInfo.InvariantCulture, $" width=\"{file.Width}\" height=\"{file.Height}\"");
    }

    private static void AppendTheme(StringBuilder builder, string? theme)
    {
        if (theme != null)
        {
            builder.Append(" data-theme=\"").Append(theme).Append('"');
        }
    }

    private static string SrcSet(List<ImageFile> group)
    {
        return group.Count == 1
            ? group[0].Url
            : string.Join(", ", group.Select(x => string.Create(CultureInfo.InvariantCulture, $"{x.Url} {x.Width}w")));
    }

    private static string? ContentType(string format) => format switch
    {
        "webp" => ImageFormats.Webp.ContentType,
        "avif" => ImageFormats.Avif.ContentType,
        "png" => ImageFormats.Png.ContentType,
        "jpeg" => ImageFormats.Jpeg.ContentType,
        "gif" => ImageFormats.Gif.ContentType,
        "svg" => ImageFormats.Svg.ContentType,
        _ => null,
    };

    public static string Encode(string value) => HeadingScanner.EncodeAttribute(value);
}
