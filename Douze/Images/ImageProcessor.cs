using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Douze.Workflow;
using NetVips;

namespace Douze.Images;

/// <summary>
/// The bridge to libvips (NetVips): reads image sizes, encodes variants, rasterizes SVG and lays text over images.
/// One per build (<see cref="BuildContext.Images"/>), created on first use: a build that uses no image feature never
/// needs the native library. Thread safe: the engine runs units in parallel, so libvips runs each operation on one
/// thread (<c>Concurrency = 1</c>) and keeps no operation cache (<c>Cache.Max = 0</c>, which would keep the sources of
/// recent operations alive). Variants are memoized by source content and variant for the build, so that the pages
/// using one image (and the fallback copies of a page) encode it once.
/// </summary>
internal sealed class ImageProcessor
{
    private static readonly Lock InitializationLock = new();
    private static bool _initialized;

    // libvips warnings are process wide: they go to the view of the build that created the last processor.
    private static Action<string>? _warn;
    private static Log.LogDelegate? _logHandler;

    // libvips' largest image dimension (VIPS_MAX_COORD).
    private const int MaxCoordinate = 10_000_000;

    /// <summary>
    /// The most memory the memoized rasters of a build use (<see cref="Rasterize"/>): 256 MB, about 80 backgrounds of
    /// 1200 by 630.
    /// </summary>
    public const long RasterBudget = 256L * 1024 * 1024;

    private readonly ConcurrentDictionary<(Hash128 Source, ImageVariant Variant), Lazy<Task<ProcessedImage>>> _variants = new();
    private readonly ConcurrentDictionary<(Hash128 Svg, int Width, int Height), Lazy<Raster>> _rasters = new();
    private long _rasterBytes;

    private ImageProcessor()
    {
    }

    /// <summary>
    /// The version of libvips, part of what the image steps' results depend on.
    /// </summary>
    public string Version { get; } = LibraryVersion;

    /// <summary>
    /// Creates the processor of a build. <paramref name="warn"/> receives the warnings of libvips.
    /// </summary>
    /// <exception cref="DouzeException">libvips cannot be loaded; the message says how to install it.</exception>
    public static ImageProcessor Create(Action<string>? warn = null)
    {
        // Reading the field runs the module initializer of NetVips, which loads libvips and records a failure.
        if (!ModuleInitializer.VipsInitialized)
        {
            throw new DouzeException(MissingLibraryMessage(ModuleInitializer.Exception));
        }

        lock (InitializationLock)
        {
            if (!_initialized)
            {
                Cache.Max = 0;
                NetVips.NetVips.Concurrency = 1;

                _logHandler = static (domain, level, message) => _warn?.Invoke(message);
                Log.SetLogHandler("VIPS", Enums.LogLevelFlags.Warning, _logHandler);
                _initialized = true;
            }

            _warn = warn;
        }

        return new ImageProcessor();
    }

    /// <summary>
    /// How to get libvips: every package and release archive of douze includes it for the platforms it ships for, builds
    /// from source on other platforms do not.
    /// </summary>
    public const string InstallHint = "douze includes it on win-x64, win-arm64, linux-x64, linux-arm64, osx-x64 and "
        + "osx-arm64; on other platforms, put libvips 8.18 (libvips.so.42, libvips.42.dylib or libvips-42.dll) next to the "
        + "douze executable or on the library path";

    /// <summary>
    /// libvips, which the image steps need.
    /// </summary>
    public static Requirement Requirement { get; } = Requirement.Create("libvips", InstallHint, static _ =>
        ValueTask.FromResult(ModuleInitializer.VipsInitialized
            ? RequirementStatus.Found(LibraryVersion)
            : RequirementStatus.Missing($"cannot be loaded{Reason(ModuleInitializer.Exception)}")));

    /// <summary>
    /// The message of a missing libvips.
    /// </summary>
    public static string MissingLibraryMessage(Exception? exception)
    {
        return $"image processing needs libvips, which cannot be loaded{Reason(exception)}. {char.ToUpperInvariant(InstallHint[0])}{InstallHint[1..]}";
    }

    private static string Reason(Exception? exception) => exception?.Message is { Length: > 0 } message ? $" ({FirstSentence(message)})" : "";

    private static string LibraryVersion => string.Create(CultureInfo.InvariantCulture, $"{NetVips.NetVips.Version(0)}.{NetVips.NetVips.Version(1)}.{NetVips.NetVips.Version(2)}");

    private static string FirstSentence(string message)
    {
        var line = message.Split('\n')[0].Trim();
        var end = line.IndexOf(". ", StringComparison.Ordinal);
        return end > 0 ? line[..end] : line.TrimEnd('.');
    }

    /// <summary>
    /// The size, format and alpha of an image. GIF and SVG are read without decoding (<see cref="ImageFormats.ReadSize"/>).
    /// </summary>
    /// <exception cref="DouzeException">The bytes are not an image libvips reads.</exception>
    public ImageInfo Probe(ReadOnlyMemory<byte> source)
    {
        var format = ImageFormats.Sniff(source.Span) ?? throw new DouzeException("not an image format douze reads (png, jpeg, webp, avif, gif, svg)");

        if (format.CopiedAsIs)
        {
            var (width, height) = ImageFormats.ReadSize(format, source.Span);
            return new ImageInfo(width, height, format.Name, HasAlpha: true);
        }

        return Run(() =>
        {
            using var image = Image.NewFromBuffer(source.Span, access: Enums.Access.Sequential);

            // Variants are auto rotated: a portrait photo stored as landscape with an orientation tag is displayed portrait.
            var rotated = image.GetTypeOf("orientation") != 0 && image.Get("orientation") is int orientation && orientation is >= 5 and <= 8;

            return rotated
                ? new ImageInfo(image.Height, image.Width, format.Name, image.HasAlpha())
                : new ImageInfo(image.Width, image.Height, format.Name, image.HasAlpha());
        });
    }

    /// <summary>
    /// A variant of a source, memoized by source content and variant for the build and encoded on the thread pool: the
    /// pages that use one image (and the fallback copies of a page) encode it once, and a page that uses several
    /// encodes them in parallel when it starts them before awaiting them (<see cref="ImageBuilder.Prefetch"/>).
    /// </summary>
    /// <exception cref="DouzeException">The format cannot be encoded, libvips cannot read the source or encode the variant.</exception>
    public Task<ProcessedImage> VariantAsync(ReadOnlyMemory<byte> source, ImageVariant variant)
    {
        var format = ImageFormats.Encodable(variant.Format) ?? throw new DouzeException($"cannot encode images as '{variant.Format}'; expected {ImageFormats.EncodableNames}");
        var key = (Hash128.Of(source.Span), variant);

        return _variants.GetOrAdd(
            key,
            static (key, state) => new Lazy<Task<ProcessedImage>>(() => Task.Run(() => Encode(state.Source, key.Variant, state.Format)), LazyThreadSafetyMode.ExecutionAndPublication),
            (Source: source, Format: format)).Value;
    }

    /// <summary>
    /// Encodes a variant: shrink on load, auto rotation and premultiplied alpha (<c>thumbnail</c>), never wider than the
    /// source, metadata stripped. Not memoized: for a source processed once (the <c>images</c> step), whose variants
    /// would only stay in memory.
    /// </summary>
    /// <exception cref="DouzeException">The format cannot be encoded, libvips cannot read the source or encode the variant.</exception>
    public ProcessedImage Encode(ReadOnlyMemory<byte> source, ImageVariant variant)
    {
        var format = ImageFormats.Encodable(variant.Format) ?? throw new DouzeException($"cannot encode images as '{variant.Format}'; expected {ImageFormats.EncodableNames}");
        return Encode(source, variant, format);
    }

    private static ProcessedImage Encode(ReadOnlyMemory<byte> source, ImageVariant variant, ImageFormat format)
    {
        return Run(() =>
        {
            // A thumbnail is a sequential pipeline written once: one per output. The height is unbounded (it defaults
            // to the width, which would fit a portrait image in a square).
            var image = Image.ThumbnailBuffer(ToArray(source), variant.Width ?? MaxCoordinate, height: MaxCoordinate, size: Enums.Size.Down);

            try
            {
                if (format == ImageFormats.Jpeg && image.HasAlpha())
                {
                    var flattened = image.Flatten(background: [255, 255, 255]);
                    image.Dispose();
                    image = flattened;
                }

                var bytes = image.WriteToBuffer(ImageFormats.SaveOptions(format, variant.Quality));
                return new ProcessedImage(bytes, image.Width, image.Height);
            }
            finally
            {
                image.Dispose();
            }
        });
    }

    /// <summary>
    /// Rasterizes an SVG at <paramref name="width"/> by <paramref name="height"/> (stretched when its aspect differs),
    /// as sRGB pixels with an alpha channel. Memoized by SVG and size for the build, within
    /// <see cref="RasterBudget"/>: the social images of pages with the same background SVG rasterize it once.
    /// </summary>
    /// <exception cref="DouzeException">libvips cannot read the SVG.</exception>
    public Raster Rasterize(string svg, int width, int height)
    {
        var key = (Hash128.OfText(svg), width, height);

        if (_rasters.TryGetValue(key, out var memoized))
        {
            return memoized.Value;
        }

        var size = (long)width * height * 4;

        if (Interlocked.Add(ref _rasterBytes, size) > RasterBudget)
        {
            Interlocked.Add(ref _rasterBytes, -size);
            return RasterizeOnce(svg, width, height);
        }

        var lazy = _rasters.GetOrAdd(key, static (key, svg) => new Lazy<Raster>(() => RasterizeOnce(svg, key.Width, key.Height), LazyThreadSafetyMode.ExecutionAndPublication), svg);
        return lazy.Value;
    }

    private static Raster RasterizeOnce(string svg, int width, int height)
    {
        return Run(() =>
        {
            using var image = Image.ThumbnailBuffer(Encoding.UTF8.GetBytes(svg), width, height: height, size: Enums.Size.Force);
            using var rgba = WithAlpha(image);
            using var bytes = rgba.Format == Enums.BandFormat.Uchar ? rgba.Copy() : rgba.Cast(Enums.BandFormat.Uchar);
            return new Raster(bytes.WriteToMemory<byte>(), bytes.Width, bytes.Height, bytes.Xres, bytes.Yres);
        });
    }

    /// <summary>
    /// Renders each text block with Pango and composites it over <paramref name="background"/>, as a PNG.
    /// </summary>
    /// <exception cref="DouzeException">A color is invalid, or a font file cannot be loaded.</exception>
    public ReadOnlyMemory<byte> Compose(Raster background, IReadOnlyList<TextBlock> blocks)
    {
        var colors = blocks.Select(x => ParseColor(x.Color)).ToArray();

        return Run(() =>
        {
            using var loaded = Image.NewFromMemory(background.Pixels, background.Width, background.Height, 4, Enums.BandFormat.Uchar);
            var canvas = loaded.Copy(interpretation: Enums.Interpretation.Srgb, xres: background.XResolution, yres: background.YResolution);

            try
            {
                for (var i = 0; i < blocks.Count; i++)
                {
                    var block = blocks[i];

                    if (string.IsNullOrWhiteSpace(block.Text))
                    {
                        continue;
                    }

                    using var layer = TextLayer(block, colors[i]);

                    if (layer == null)
                    {
                        continue;
                    }

                    var x = block.Align switch
                    {
                        "center" or "centre" => block.X + (block.Width - layer.Width) / 2,
                        "right" => block.X + block.Width - layer.Width,
                        _ => block.X,
                    };

                    var composed = canvas.Composite2(layer, Enums.BlendMode.Over, x: x, y: block.Y);
                    canvas.Dispose();
                    canvas = composed;
                }

                return (ReadOnlyMemory<byte>)canvas.WriteToBuffer(".png[keep=none]");
            }
            finally
            {
                canvas.Dispose();
            }
        });
    }

    /// <summary>
    /// The text of a block as a colored layer with an alpha channel, wrapped at its width and clipped at its box; null
    /// when it renders nothing.
    /// </summary>
    private static Image? TextLayer(TextBlock block, (double R, double G, double B, double A) color)
    {
        var align = block.Align switch
        {
            "center" or "centre" => Enums.Align.Centre,
            "right" => Enums.Align.High,
            _ => Enums.Align.Low,
        };

        // A one band mask; the text is plain text, not Pango markup. 72 dpi: a font size in points is in pixels.
        using var rendered = Image.Text(EscapeMarkup(block.Text), font: block.Font, width: block.Width, align: align, dpi: 72, fontfile: block.FontFile);

        if (rendered.Width == 0 || rendered.Height == 0)
        {
            return null;
        }

        // As few libvips operations as possible: each takes process wide locks, which the parallel units contend for.
        using var cropped = rendered.Width > block.Width || rendered.Height > block.Height
            ? rendered.Crop(0, 0, Math.Min(rendered.Width, block.Width), Math.Min(rendered.Height, block.Height))
            : null;

        var mask = cropped ?? rendered;

        using var scaled = color.A >= 255 ? null : mask * (color.A / 255);
        using var faded = scaled?.Cast(Enums.BandFormat.Uchar);
        using var fill = Fill(mask.Width, mask.Height, color);
        using var joined = fill.Bandjoin(faded ?? mask);

        return joined.Copy(interpretation: Enums.Interpretation.Srgb);
    }

    /// <summary>
    /// A three band image of one color, made from memory rather than by a chain of operations.
    /// </summary>
    private static Image Fill(int width, int height, (double R, double G, double B, double A) color)
    {
        var pixels = new byte[width * height * 3];
        var pattern = pixels.AsSpan();
        byte[] rgb = [(byte)color.R, (byte)color.G, (byte)color.B];

        for (var i = 0; i < pixels.Length; i += 3)
        {
            rgb.CopyTo(pattern[i..]);
        }

        return Image.NewFromMemory(pixels, width, height, 3, Enums.BandFormat.Uchar);
    }

    /// <summary>
    /// An sRGB image with an alpha channel. A synthesized or grey image is copied with the sRGB interpretation, which
    /// compositing needs.
    /// </summary>
    private static Image WithAlpha(Image image)
    {
        using var srgb = image.Interpretation == Enums.Interpretation.Srgb ? image.Copy() : image.Colourspace(Enums.Interpretation.Srgb);

        if (srgb.HasAlpha())
        {
            return srgb.Copy(interpretation: Enums.Interpretation.Srgb);
        }

        using var joined = srgb.Bandjoin(255);
        return joined.Cast(Enums.BandFormat.Uchar).Copy(interpretation: Enums.Interpretation.Srgb);
    }

    private static string EscapeMarkup(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>
    /// <c>#rgb</c>, <c>#rgba</c>, <c>#rrggbb</c> or <c>#rrggbbaa</c>.
    /// </summary>
    /// <exception cref="DouzeException">The color is not one of these.</exception>
    public static (double R, double G, double B, double A) ParseColor(string color)
    {
        var hex = color.Trim().TrimStart('#');

        if (hex.Length is 3 or 4)
        {
            hex = string.Concat(hex.Select(x => $"{x}{x}"));
        }

        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            throw new DouzeException($"invalid color '{color}'; expected #rrggbb or #rrggbbaa");
        }

        if (hex.Length == 6)
        {
            value = (value << 8) | 0xFF;
        }

        return ((value >> 24) & 0xFF, (value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
    }

    private static byte[] ToArray(ReadOnlyMemory<byte> memory)
    {
        return MemoryMarshal.TryGetArray(memory, out var segment) && segment.Offset == 0 && segment.Count == segment.Array!.Length
            ? segment.Array
            : memory.ToArray();
    }

    /// <summary>
    /// Runs a libvips operation, its errors as <see cref="DouzeException"/>s (the first line of the libvips message).
    /// </summary>
    private static T Run<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (VipsException e)
        {
            var message = e.Message.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            throw new DouzeException(string.Join(": ", message.Distinct()));
        }
    }
}
