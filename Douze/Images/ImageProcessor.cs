using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Douze.Workflow;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Image = SixLabors.ImageSharp.Image;

namespace Douze.Images;

/// <summary>
/// The image engine (ImageSharp, managed): reads image sizes, encodes variants, rasterizes SVG (<see cref="SvgRenderer"/>)
/// and lays text over images (<see cref="FontResolver"/>). One per build (<see cref="BuildContext.Images"/>), created
/// on first use. Thread safe: the engine runs units in parallel, so ImageSharp runs each operation on one thread
/// (<see cref="Configuration.MaxDegreeOfParallelism"/> of 1). Variants are memoized by source content and variant for
/// the build, so that the pages using one image (and the fallback copies of a page) encode it once.
/// </summary>
internal sealed class ImageProcessor
{
    /// <summary>
    /// The configuration of every operation: one thread each, since the engine parallelizes per unit.
    /// </summary>
    internal static readonly Configuration Configuration = CreateConfiguration();

    /// <summary>
    /// The most memory the memoized rasters of a build use (<see cref="Rasterize"/>): 256 MB, about 80 backgrounds of
    /// 1200 by 630.
    /// </summary>
    public const long RasterBudget = 256L * 1024 * 1024;

    // The largest dimension of a variant: an unbounded height scales with the width.
    private const int MaxDimension = 65_535;

    private readonly ConcurrentDictionary<(Hash128 Source, ImageVariant Variant), Lazy<Task<ProcessedImage>>> _variants = new();
    private readonly ConcurrentDictionary<(Hash128 Svg, int Width, int Height), Lazy<Raster>> _rasters = new();
    private long _rasterBytes;

    private ImageProcessor()
    {
    }

    /// <summary>
    /// The version of ImageSharp, part of what the image steps' results depend on.
    /// </summary>
    public string Version { get; } = LibraryVersion;

    /// <summary>
    /// The fonts of the text of social images: the system's and the font files of the site.
    /// </summary>
    public FontResolver Fonts { get; } = new();

    /// <summary>
    /// Creates the processor of a build.
    /// </summary>
    public static ImageProcessor Create() => new();

    internal static string LibraryVersion { get; } = typeof(Image).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(Image).Assembly.GetName().Version?.ToString(3)
        ?? "";

    private static Configuration CreateConfiguration()
    {
        var configuration = Configuration.Default.Clone();
        configuration.MaxDegreeOfParallelism = 1;
        return configuration;
    }

    /// <summary>
    /// The size, format and alpha of an image. GIF and SVG are read without decoding (<see cref="ImageFormats.ReadSize"/>).
    /// </summary>
    /// <exception cref="DouzeException">The bytes are not an image douze reads.</exception>
    public ImageInfo Probe(ReadOnlyMemory<byte> source)
    {
        var format = ImageFormats.Sniff(source.Span) ?? throw new DouzeException($"not an image format douze reads ({ImageFormats.ReadNames})");

        if (format.CopiedAsIs)
        {
            var (width, height) = ImageFormats.ReadSize(format, source.Span);
            return new ImageInfo(width, height, format.Name, HasAlpha: true);
        }

        return Run(() =>
        {
            var info = Image.Identify(new DecoderOptions { Configuration = Configuration }, source.Span);
            var hasAlpha = info.PixelType.AlphaRepresentation != PixelAlphaRepresentation.None;

            // Variants are auto rotated: a portrait photo stored as landscape with an orientation tag is displayed portrait.
            return IsTransposed(info)
                ? new ImageInfo(info.Height, info.Width, format.Name, hasAlpha)
                : new ImageInfo(info.Width, info.Height, format.Name, hasAlpha);
        });
    }

    /// <summary>
    /// A variant of a source, memoized by source content and variant for the build and encoded on the thread pool: the
    /// pages that use one image (and the fallback copies of a page) encode it once, and a page that uses several
    /// encodes them in parallel when it starts them before awaiting them (<see cref="ImageBuilder.Prefetch"/>).
    /// </summary>
    /// <exception cref="DouzeException">The format cannot be encoded, the source cannot be read or the variant encoded.</exception>
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
    /// Encodes a variant: scaled on load where the format allows it (JPEG), auto rotated, resized with Lanczos 3 in
    /// premultiplied alpha, converted to sRGB from an embedded color profile, never wider than the source, metadata
    /// stripped. Not memoized: for a source processed once (the <c>images</c> step), whose variants would only stay in
    /// memory.
    /// </summary>
    /// <exception cref="DouzeException">The format cannot be encoded, the source cannot be read or the variant encoded.</exception>
    public ProcessedImage Encode(ReadOnlyMemory<byte> source, ImageVariant variant)
    {
        var format = ImageFormats.Encodable(variant.Format) ?? throw new DouzeException($"cannot encode images as '{variant.Format}'; expected {ImageFormats.EncodableNames}");
        return Encode(source, variant, format);
    }

    private static ProcessedImage Encode(ReadOnlyMemory<byte> source, ImageVariant variant, ImageFormat format)
    {
        if (ImageFormats.Sniff(source.Span) is not { CopiedAsIs: false })
        {
            throw new DouzeException($"not an image format douze encodes variants of ({ImageFormats.DecodableNames})");
        }

        return Run(() =>
        {
            var info = Image.Identify(new DecoderOptions { Configuration = Configuration }, source.Span);
            var transposed = IsTransposed(info);
            var hasAlpha = info.PixelType.AlphaRepresentation != PixelAlphaRepresentation.None;
            var (width, height) = transposed ? (info.Height, info.Width) : (info.Width, info.Height);

            // The size of the variant as displayed: the requested width, never wider than the source.
            var targetWidth = Math.Min(variant.Width ?? width, width);
            var targetHeight = targetWidth == width ? height : Math.Clamp((int)Math.Round(height * (double)targetWidth / width, MidpointRounding.AwayFromZero), 1, MaxDimension);

            var options = new DecoderOptions
            {
                Configuration = Configuration,
                ColorProfileHandling = ColorProfileHandling.Convert,
                Sampler = KnownResamplers.Lanczos3,
                MaxFrames = 1,

                // The decoder scales on load to fit this box, in the orientation of the stored pixels.
                TargetSize = targetWidth == width ? null : transposed ? new Size(targetHeight, targetWidth) : new Size(targetWidth, targetHeight),
            };

            using var image = Image.Load(options, source.Span);

            image.Mutate(x =>
            {
                x.AutoOrient();

                if (image.Width != targetWidth || image.Height != targetHeight)
                {
                    x.Resize(new ResizeOptions { Size = new Size(targetWidth, targetHeight), Mode = ResizeMode.Stretch, Sampler = KnownResamplers.Lanczos3, PremultiplyAlpha = true });
                }

                if (format == ImageFormats.Jpeg && hasAlpha)
                {
                    x.BackgroundColor(Color.White);
                }
            });

            using var output = new MemoryStream();
            image.Save(output, Encoder(format, variant.Quality, hasAlpha && format != ImageFormats.Jpeg));
            return new ProcessedImage(output.ToArray(), image.Width, image.Height);
        });
    }

    /// <summary>
    /// The encoder of a variant. Metadata (EXIF, XMP, ICC) is never kept; PNG is written as 8 bit truecolor, as the
    /// source's palette or bit depth would not fit a resized image.
    /// </summary>
    internal static ImageEncoder Encoder(ImageFormat format, int quality, bool hasAlpha)
    {
        if (format == ImageFormats.Webp)
        {
            return new WebpEncoder { FileFormat = WebpFileFormatType.Lossy, Quality = quality, SkipMetadata = true };
        }

        if (format == ImageFormats.Jpeg)
        {
            return new JpegEncoder { Quality = quality, SkipMetadata = true };
        }

        if (format == ImageFormats.Png)
        {
            return new PngEncoder
            {
                ColorType = hasAlpha ? PngColorType.RgbWithAlpha : PngColorType.Rgb,
                BitDepth = PngBitDepth.Bit8,
                SkipMetadata = true,
            };
        }

        throw new InvalidOperationException($"no encoder for '{format.Name}'");
    }

    /// <summary>
    /// Rasterizes an SVG at <paramref name="width"/> by <paramref name="height"/> (stretched when its aspect differs).
    /// Memoized by SVG and size for the build, within <see cref="RasterBudget"/>: the social images of pages with the
    /// same background SVG rasterize it once.
    /// </summary>
    /// <exception cref="DouzeException">The SVG cannot be read, or uses what <see cref="SvgRenderer"/> does not render.</exception>
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

    private static Raster RasterizeOnce(string svg, int width, int height) => new(Run(() => SvgRenderer.Render(svg, width, height)));

    /// <summary>
    /// Lays out each text block and draws it over <paramref name="background"/>, as a PNG.
    /// </summary>
    /// <exception cref="DouzeException">A color is invalid, a font cannot be found or a font file read.</exception>
    public ReadOnlyMemory<byte> Compose(Raster background, IReadOnlyList<TextBlock> blocks)
    {
        var colors = blocks.Select(x => ParseColor(x.Color)).ToArray();
        var fonts = blocks.Select(x => string.IsNullOrWhiteSpace(x.Text) ? null : Fonts.Resolve(x.Font, x.FontFile)).ToArray();

        return Run(() =>
        {
            using var canvas = background.Image.Clone(Configuration);

            canvas.Mutate(context => context.Paint(drawing =>
            {
                for (var i = 0; i < blocks.Count; i++)
                {
                    if (fonts[i] is not { } font)
                    {
                        continue;
                    }

                    var block = blocks[i];
                    var (r, g, b, a) = colors[i];

                    // 72 dpi: a font size in points is in pixels. The text wraps at the block's width and is aligned in it.
                    // Lines are the font's line height apart (ascender, descender and line gap), as browsers and Pango
                    // space them, not one em.
                    var metrics = font.FontMetrics;
                    var options = new RichTextOptions(font)
                    {
                        Origin = new System.Numerics.Vector2(block.X, block.Y),
                        WrappingLength = block.Width,
                        Dpi = 72,
                        LineSpacing = metrics.HorizontalMetrics.LineHeight > 0 ? metrics.HorizontalMetrics.LineHeight / (float)metrics.UnitsPerEm : 1,
                        TextAlignment = block.Align switch
                        {
                            "center" or "centre" => TextAlignment.Center,
                            "right" => TextAlignment.End,
                            _ => TextAlignment.Start,
                        },
                    };

                    drawing.Save();
                    drawing.Clip(new RectanglePolygon(block.X, block.Y, block.Width, block.Height));
                    drawing.DrawText(options, block.Text, Brushes.Solid(Color.FromPixel(new Rgba32((byte)r, (byte)g, (byte)b, (byte)a))), null);
                    drawing.Restore();
                }
            }));

            using var output = new MemoryStream();
            canvas.Save(output, Encoder(ImageFormats.Png, 0, hasAlpha: true));
            return (ReadOnlyMemory<byte>)output.ToArray();
        });
    }

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

    /// <summary>
    /// Whether the EXIF orientation of an image swaps its width and height (orientations 5 to 8).
    /// </summary>
    private static bool IsTransposed(SixLabors.ImageSharp.ImageInfo info)
    {
        return info.Metadata.ExifProfile is { } exif
            && exif.TryGetValue(ExifTag.Orientation, out var orientation)
            && orientation.Value is >= 5 and <= 8;
    }

    /// <summary>
    /// Runs an operation, the errors of ImageSharp as <see cref="DouzeException"/>s.
    /// </summary>
    private static T Run<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (UnknownImageFormatException)
        {
            // Its message lists every decoder of ImageSharp.
            throw new DouzeException("the image cannot be decoded (corrupt, or a format douze does not read)");
        }
        catch (Exception e) when (e is ImageFormatException or NotSupportedException or InvalidImageContentException)
        {
            throw new DouzeException(e.Message.ReplaceLineEndings(" ").TrimEnd('.'));
        }
    }
}
