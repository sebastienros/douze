using System.Collections.Concurrent;
using System.Globalization;
using Douze.Workflow;
using SixLabors.Fonts;

namespace Douze.Images;

/// <summary>
/// Resolves the font descriptions of social images: <c>[family list] [style words] [size]</c>, like
/// <c>Outfit Bold 56</c> or <c>Inter, sans italic 24</c>. The families are tried in order, each among the font file of
/// the block, then the fonts of the system (the generic families <c>sans</c>, <c>serif</c> and <c>monospace</c> resolve
/// to common fonts of each platform); a description no family of which is found uses the font file when there is one.
/// Style words: <c>italic</c> and <c>oblique</c>, and weights from <c>thin</c> to <c>black</c>: <c>semibold</c> and
/// above are bold. A weight word is first tried as part of the family name (<c>Outfit Light</c>), the name of the
/// family of a font file of that weight. The size is in pixels (an optional <c>px</c>), 16 by default. Thread safe: font
/// files are loaded once per build, by content hash.
/// </summary>
internal sealed class FontResolver
{
    private static readonly Dictionary<string, (FontStyle Style, bool IsWeight)> StyleWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["normal"] = (FontStyle.Regular, false),
        ["roman"] = (FontStyle.Regular, false),
        ["italic"] = (FontStyle.Italic, false),
        ["oblique"] = (FontStyle.Italic, false),
        ["thin"] = (FontStyle.Regular, true),
        ["ultralight"] = (FontStyle.Regular, true),
        ["ultra-light"] = (FontStyle.Regular, true),
        ["extralight"] = (FontStyle.Regular, true),
        ["extra-light"] = (FontStyle.Regular, true),
        ["light"] = (FontStyle.Regular, true),
        ["semilight"] = (FontStyle.Regular, true),
        ["semi-light"] = (FontStyle.Regular, true),
        ["book"] = (FontStyle.Regular, true),
        ["regular"] = (FontStyle.Regular, true),
        ["medium"] = (FontStyle.Regular, true),
        ["semibold"] = (FontStyle.Bold, true),
        ["semi-bold"] = (FontStyle.Bold, true),
        ["demibold"] = (FontStyle.Bold, true),
        ["demi-bold"] = (FontStyle.Bold, true),
        ["bold"] = (FontStyle.Bold, true),
        ["extrabold"] = (FontStyle.Bold, true),
        ["extra-bold"] = (FontStyle.Bold, true),
        ["ultrabold"] = (FontStyle.Bold, true),
        ["ultra-bold"] = (FontStyle.Bold, true),
        ["heavy"] = (FontStyle.Bold, true),
        ["black"] = (FontStyle.Bold, true),
    };

    private static readonly Dictionary<string, string[]> GenericFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sans"] = ["DejaVu Sans", "Liberation Sans", "Noto Sans", "Arial", "Helvetica", "Helvetica Neue", "Segoe UI", "Verdana", "Ubuntu", "Cantarell", "FreeSans"],
        ["serif"] = ["DejaVu Serif", "Liberation Serif", "Noto Serif", "Times New Roman", "Times", "Georgia", "FreeSerif"],
        ["monospace"] = ["DejaVu Sans Mono", "Liberation Mono", "Noto Sans Mono", "Consolas", "Menlo", "Courier New", "Courier", "FreeMono"],
    };

    private static readonly Lazy<IReadOnlyFontCollection?> System = new(() =>
    {
        try
        {
            return SystemFonts.Collection;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    });

    private readonly ConcurrentDictionary<Hash128, Lazy<FontFamily>> _files = new();

    static FontResolver()
    {
        GenericFamilies["sans-serif"] = GenericFamilies["sans"];
        GenericFamilies["mono"] = GenericFamilies["monospace"];
    }

    /// <summary>
    /// The font of a description.
    /// </summary>
    /// <exception cref="DouzeException">The description is invalid, its font is not found or the font file cannot be read.</exception>
    public Font Resolve(string description, FontSource? file)
    {
        var (families, style, weight, size) = Parse(description);
        var own = file == null ? (FontFamily?)null : Load(file);

        foreach (var name in families)
        {
            if (weight != null && Find($"{name} {weight}", own) is { } weighted)
            {
                return Create(weighted, style is FontStyle.Italic or FontStyle.BoldItalic ? FontStyle.Italic : FontStyle.Regular, size);
            }

            if (Find(name, own) is { } family)
            {
                return Create(family, style, size);
            }
        }

        if (own is { } fallback)
        {
            return Create(fallback, style, size);
        }

        throw new DouzeException($"font '{description}' not found; set 'fontfile' to a font file of the site, or install the font");
    }

    /// <summary>
    /// The families, style, weight word and size of a description.
    /// </summary>
    /// <exception cref="DouzeException">The size is not a positive number.</exception>
    internal static (IReadOnlyList<string> Families, FontStyle Style, string? Weight, float Size) Parse(string description)
    {
        var words = description.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var size = 16f;

        if (words.Count > 0 && TrySize(words[^1], out var parsed))
        {
            size = parsed;
            words.RemoveAt(words.Count - 1);
        }
        else if (words.Count > 0 && words[^1].Length > 0 && char.IsAsciiDigit(words[^1][0]))
        {
            throw new DouzeException($"invalid font size '{words[^1]}' in font '{description}'");
        }

        var bold = false;
        var italic = false;
        string? weight = null;

        // Style words end the description, after the families: 'Outfit Bold', 'sans italic bold'.
        while (words.Count > 1 && StyleWords.TryGetValue(words[^1], out var word))
        {
            bold |= word.Style == FontStyle.Bold;
            italic |= word.Style == FontStyle.Italic;

            if (word.IsWeight && !words[^1].Equals("regular", StringComparison.OrdinalIgnoreCase))
            {
                weight ??= words[^1];
            }

            words.RemoveAt(words.Count - 1);
        }

        var families = string.Join(' ', words).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var style = (bold, italic) switch
        {
            (true, true) => FontStyle.BoldItalic,
            (true, false) => FontStyle.Bold,
            (false, true) => FontStyle.Italic,
            _ => FontStyle.Regular,
        };

        return (families.Length == 0 ? ["sans"] : families, style, weight, size);
    }

    private static bool TrySize(string word, out float size)
    {
        var number = word.EndsWith("px", StringComparison.OrdinalIgnoreCase) ? word[..^2] : word;
        return float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out size) && size > 0 && size < 10_000;
    }

    private static FontFamily? Find(string name, FontFamily? own)
    {
        if (own is { } file && file.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return file;
        }

        if (System.Value is not { } system)
        {
            return null;
        }

        if (system.TryGet(name, out var family))
        {
            return family;
        }

        if (GenericFamilies.TryGetValue(name, out var candidates))
        {
            foreach (var candidate in candidates)
            {
                if (system.TryGet(candidate, out family))
                {
                    return family;
                }
            }

            if (SystemFonts.TryGetDefaultFamilyName(out var fallback) && system.TryGet(fallback, out family))
            {
                return family;
            }
        }

        return null;
    }

    /// <summary>
    /// A font of a family in a style, or in the closest style it has: a font file of one style is used for every style.
    /// </summary>
    private static Font Create(FontFamily family, FontStyle style, float size)
    {
        var available = family.GetAvailableStyles().ToArray();

        if (available.Length == 0 || available.Contains(style))
        {
            return family.CreateFont(size, style);
        }

        FontStyle[] order = style switch
        {
            FontStyle.BoldItalic => [FontStyle.Bold, FontStyle.Italic, FontStyle.Regular],
            FontStyle.Bold => [FontStyle.BoldItalic, FontStyle.Regular, FontStyle.Italic],
            FontStyle.Italic => [FontStyle.BoldItalic, FontStyle.Regular, FontStyle.Bold],
            _ => [FontStyle.Bold, FontStyle.Italic, FontStyle.BoldItalic],
        };

        return family.CreateFont(size, order.FirstOrDefault(available.Contains, available[0]));
    }

    private FontFamily Load(FontSource file)
    {
        return _files.GetOrAdd(file.Hash, static (_, file) => new Lazy<FontFamily>(() =>
        {
            try
            {
                using var stream = new MemoryStream(file.Bytes.ToArray(), writable: false);
                return new FontCollection().Add(stream);
            }
            catch (Exception e) when (e is InvalidFontFileException or InvalidFontTableException or MissingFontTableException or EndOfStreamException or NotSupportedException)
            {
                throw new DouzeException($"cannot read the font file ({e.Message.TrimEnd('.')})");
            }
        }, LazyThreadSafetyMode.ExecutionAndPublication), file).Value;
    }
}
