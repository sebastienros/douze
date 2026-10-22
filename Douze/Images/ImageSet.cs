using System.Collections.Immutable;
using Douze.FileSystems;
using Douze.Markdown;
using Douze.Nodes;
using Douze.Workflow;
using Douze.Workflow.Steps;

namespace Douze.Images;

/// <summary>
/// A file produced from a source image: its url, size and format name.
/// </summary>
internal sealed record ImageFile(string Url, int Width, int Height, string Format);

/// <summary>
/// What became of a source image: its url in the source tree (<see cref="Source"/>), its size, the fingerprinted copy
/// of the original when there is one (<see cref="Url"/>), and its variants, by format in the order requested then by
/// width ascending; the last format is the fallback of the markup. An SVG or a GIF has one variant: its copy.
/// </summary>
internal sealed record ImageSet(string Source, int Width, int Height, string Format, string? Url, ImmutableArray<ImageFile> Variants)
{
    /// <summary>
    /// <c>{ source, url, width, height, format, variants: [{ url, width, height, format }] }</c>, the <c>image</c>
    /// metadata of the <c>images</c> step and the values of <c>images</c>.
    /// </summary>
    public NodeObject ToNode()
    {
        var variants = new NodeArray();

        foreach (var variant in Variants)
        {
            variants.Add(new NodeObject
            {
                ["url"] = NodeValue.Create(variant.Url),
                ["width"] = NodeValue.Create(variant.Width),
                ["height"] = NodeValue.Create(variant.Height),
                ["format"] = NodeValue.Create(variant.Format),
            });
        }

        return new NodeObject
        {
            ["source"] = NodeValue.Create(Source),
            ["url"] = Url == null ? null : NodeValue.Create(Url),
            ["width"] = NodeValue.Create(Width),
            ["height"] = NodeValue.Create(Height),
            ["format"] = NodeValue.Create(Format),
            ["variants"] = variants,
        };
    }

    /// <summary>
    /// Reads back <see cref="ToNode"/>, or returns null for a node of another shape.
    /// </summary>
    public static ImageSet? FromNode(Node? node)
    {
        if (node is not NodeObject image
            || image["source"] is not NodeValue<string> source
            || Int(image["width"]) is not { } width
            || Int(image["height"]) is not { } height
            || image["variants"] is not NodeArray variants)
        {
            return null;
        }

        var files = ImmutableArray.CreateBuilder<ImageFile>(variants.Count);

        foreach (var item in variants)
        {
            if (item is not NodeObject variant
                || variant["url"] is not NodeValue<string> url
                || Int(variant["width"]) is not { } variantWidth
                || Int(variant["height"]) is not { } variantHeight
                || variant["format"] is not NodeValue<string> format)
            {
                return null;
            }

            files.Add(new ImageFile(url.Value, variantWidth, variantHeight, format.Value));
        }

        return new ImageSet(
            source.Value,
            width,
            height,
            (image["format"] as NodeValue<string>)?.Value ?? "",
            (image["url"] as NodeValue<string>)?.Value,
            files.ToImmutable());

        static int? Int(Node? node) => node switch
        {
            NodeValue<int> i => i.Value,
            NodeValue<long> l => (int)l.Value,
            NodeValue<decimal> d => (int)d.Value,
            NodeValue<double> d => (int)d.Value,
            _ => null,
        };
    }
}

/// <summary>
/// The variants to produce of an image: widths (none: the width of the source; never wider than it), formats (none:
/// the format of the source), the quality of lossy formats and the output folder. Defaults come from the
/// <c>images</c> settings of the <c>markdown</c> section.
/// </summary>
internal sealed record ImageRequest(ImmutableArray<int> Widths, ImmutableArray<string> Formats, int Quality, FPath Output)
{
    public static readonly FPath DefaultOutput = "/assets/images/";

    public static ImageRequest FromDefaults(ImageOptions options)
    {
        return new ImageRequest(
            options.Widths,
            options.Formats,
            options.Quality ?? ImageFormats.DefaultQuality,
            options.Output is { } output ? StepPaths.Directory(output) : DefaultOutput);
    }

    /// <exception cref="DouzeException">A format cannot be encoded, a width or the quality is out of range.</exception>
    public ImageRequest Validate()
    {
        foreach (var format in Formats)
        {
            if (ImageFormats.Encodable(format) == null)
            {
                throw new DouzeException($"unknown image format '{format}'; expected {ImageFormats.EncodableNames}");
            }
        }

        if (Widths.Any(x => x <= 0))
        {
            throw new DouzeException("image widths must be positive");
        }

        if (Quality is < 1 or > 100)
        {
            throw new DouzeException("image quality must be from 1 to 100");
        }

        return this;
    }
}

/// <summary>
/// A file to emit for an image: its output path, content and content type.
/// </summary>
internal sealed record ImageOutput(FPath Path, ReadOnlyMemory<byte> Bytes, string ContentType)
{
    /// <summary>
    /// The file, with empty metadata: its provenance is set by the step or the scheduler that emits it.
    /// </summary>
    public VirtualFile ToFile() => new(Path, FileContent.FromBytes(Bytes, isText: ContentType == ImageFormats.Svg.ContentType), Metadata.Empty, ContentType, Provenance.Source);
}

/// <summary>
/// Turns a source image into its variants (<see cref="ImageSet"/> and the files to emit). An SVG or a GIF is copied as
/// it is, with a fingerprinted name and the size read from the file, without libvips.
/// </summary>
internal static class ImageBuilder
{
    /// <param name="images">The processor, created only for formats that need libvips.</param>
    /// <param name="source">The url of the source image (<see cref="ImageSources.Url"/>).</param>
    /// <param name="bytes">The content of the source image.</param>
    /// <param name="request">The variants to produce.</param>
    /// <param name="original">Whether to emit the original with a fingerprinted name too (the <c>images</c> step, which
    /// processes each source once: its variants are not memoized).</param>
    /// <exception cref="DouzeException">The image cannot be read or encoded, or libvips is missing.</exception>
    public static async ValueTask<(ImageSet Set, IReadOnlyList<ImageOutput> Files)> BuildAsync(Func<ImageProcessor> images, string source, ReadOnlyMemory<byte> bytes, ImageRequest request, bool original)
    {
        var name = Path.GetFileNameWithoutExtension(source);
        var extension = Path.GetExtension(source);
        var format = ImageFormats.Sniff(bytes.Span) ?? ImageFormats.FromExtension(extension) ?? throw new DouzeException($"'{source}' is not an image format douze reads (png, jpeg, webp, avif, gif, svg)");
        var files = new List<ImageOutput>();

        string? originalUrl = null;

        if (format.CopiedAsIs || original)
        {
            var path = FPath.Combine(request.Output, ImageVariant.OriginalFileName(name, Hash128.Of(bytes.Span), extension.Length > 0 ? extension : format.Extension));
            files.Add(new ImageOutput(path, bytes, format.ContentType));
            originalUrl = path.FullName;
        }

        if (format.CopiedAsIs)
        {
            var (width, height) = ImageFormats.ReadSize(format, bytes.Span);
            return (new ImageSet(source, width, height, format.Name, originalUrl, [new ImageFile(originalUrl!, width, height, format.Name)]), files);
        }

        var processor = images();
        var (info, variants) = Plan(processor, bytes, format, request);

        // The tag and the filters may meet a source in many pages: their variants are memoized, and all of them are
        // started before the first is awaited.
        var processed = original
            ? variants.Select(x => Task.FromResult(processor.Encode(bytes, x.Variant))).ToArray()
            : variants.Select(x => processor.VariantAsync(bytes, x.Variant)).ToArray();

        var set = ImmutableArray.CreateBuilder<ImageFile>(variants.Length);

        for (var i = 0; i < variants.Length; i++)
        {
            var target = variants[i].Format;
            var image = await processed[i];
            var path = FPath.Combine(request.Output, ImageVariant.FileName(name, image.Width, Hash128.Of(image.Bytes.Span), target));

            files.Add(new ImageOutput(path, image.Bytes, target.ContentType));
            set.Add(new ImageFile(path.FullName, image.Width, image.Height, target.Name));
        }

        return (new ImageSet(source, info.Width, info.Height, info.Format, originalUrl, set.ToImmutable()), files);
    }

    /// <summary>
    /// Starts encoding the memoized variants of a source that a page is about to ask for (<see cref="BuildAsync"/> with
    /// <c>original: false</c>), without waiting for them, so that the variants of the images of a page are encoded in
    /// parallel. Errors are left to <see cref="BuildAsync"/>, which meets them again.
    /// </summary>
    public static void Prefetch(Func<ImageProcessor> images, ReadOnlyMemory<byte> bytes, ImageRequest request)
    {
        try
        {
            if (ImageFormats.Sniff(bytes.Span) is not { CopiedAsIs: false } format)
            {
                return;
            }

            var processor = images();

            foreach (var (variant, _) in Plan(processor, bytes, format, request).Variants)
            {
                _ = processor.VariantAsync(bytes, variant);
            }
        }
        catch (DouzeException)
        {
        }
    }

    /// <summary>
    /// The variants of a source: by format in the order requested, then by width ascending.
    /// </summary>
    private static (ImageInfo Info, (ImageVariant Variant, ImageFormat Format)[] Variants) Plan(ImageProcessor processor, ReadOnlyMemory<byte> bytes, ImageFormat format, ImageRequest request)
    {
        var info = processor.Probe(bytes);
        var widths = request.Widths.IsEmpty ? [info.Width] : request.Widths.Select(x => Math.Min(x, info.Width)).Distinct().Order().ToArray();
        var formats = request.Formats.IsEmpty ? [format.Name] : request.Formats.Select(x => ImageFormats.Encodable(x)!.Name).Distinct().ToArray();
        var variants = new List<(ImageVariant, ImageFormat)>(widths.Length * formats.Length);

        foreach (var formatName in formats)
        {
            var target = ImageFormats.Encodable(formatName)!;

            foreach (var width in widths)
            {
                variants.Add((new ImageVariant(width, target.Name, target.HasQuality ? request.Quality : 0), target));
            }
        }

        return (info, [.. variants]);
    }
}

/// <summary>
/// Where the source of an image is: a path relative to the page's folder, or to the content folder when it starts
/// with <c>/</c>; and the url of a source, its path in the content folder.
/// </summary>
internal static class ImageSources
{
    /// <summary>
    /// The source path of <paramref name="src"/>, or null for an external url (<c>https:</c>, <c>data:</c>, <c>//</c>)
    /// or a path that leaves the source folder. A query or a fragment is ignored.
    /// </summary>
    public static FPath? Resolve(string src, FPath directory, FPath contentFolder)
    {
        if (string.IsNullOrWhiteSpace(src) || src.StartsWith("//", StringComparison.Ordinal) || src.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }

        var end = src.IndexOfAny(['?', '#']);
        var path = Uri.UnescapeDataString(end >= 0 ? src[..end] : src);
        var segments = new List<string>();

        if (!path.StartsWith('/'))
        {
            segments.AddRange(directory.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries));
        }
        else
        {
            segments.AddRange(contentFolder.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries));
        }

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    return null;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0 || path.EndsWith('/'))
        {
            return null;
        }

        return new FPath("/" + string.Join('/', segments));
    }

    /// <summary>
    /// The url of a source image: its path without the content folder (<c>/_content/docs/a.png</c> is
    /// <c>/docs/a.png</c>), the key of <c>images</c>.
    /// </summary>
    public static string Url(FPath path, FPath contentFolder) => StepPaths.StripRoot(path, contentFolder).FullName;

    /// <summary>
    /// Reads a source image and records it as a dependency of the unit (its content hash): a changed image renders
    /// the unit again.
    /// </summary>
    /// <exception cref="DouzeException">The file does not exist.</exception>
    public static async ValueTask<ReadOnlyMemory<byte>> ReadAsync(IFileSystem source, FPath path, string src, DependencyTracker? deps)
    {
        if (!(await source.GetFileEntryAsync(path)).Exists)
        {
            throw new DouzeException($"image '{src}' not found ('{path}')");
        }

        await using var stream = await source.OpenFileAsync(path);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        var bytes = memory.ToArray();

        deps?.RecordTemplate(path, Hash128.Of(bytes));
        return bytes;
    }
}
