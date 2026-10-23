using System.Runtime.InteropServices;
using System.Text;
using NetVips;
using SkiaSharp;
using Svg.Skia;

namespace Douze.ImageBench;

/// <summary>
/// One library's take on Douze's image operations: a variant from a source in memory to bytes in memory (Douze's
/// path), the same from a source file to an output file through streams, and an SVG rasterized to a PNG.
/// </summary>
internal interface IPipeline
{
    string Name { get; }

    (byte[] Bytes, int Width, int Height) EncodeBuffer(byte[] source, Variant variant);

    Encoded EncodeStream(string input, Variant variant, string folder, string name);

    byte[] RasterizeSvg(string svg, int width, int height);
}

internal static class Pipelines
{
    public static IPipeline Create(string name) => name switch
    {
        "vips" => new VipsPipeline(),
        "skia" => new SkiaPipeline(),
        _ => throw new ArgumentException($"unknown library '{name}'"),
    };

    // Source files are read 16 KB at a time on the stream paths.
    public static FileStream OpenSource(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 16 * 1024);
}

/// <summary>
/// libvips, set up as Douze's <c>ImageProcessor</c> is: one thread per operation, no operation cache, <c>thumbnail</c>
/// (shrink on load, never wider than the source), JPEG flattened on white, metadata stripped.
/// </summary>
internal sealed class VipsPipeline : IPipeline
{
    private const int MaxCoordinate = 10_000_000;

    public VipsPipeline()
    {
        Cache.Max = 0;
        NetVips.NetVips.Concurrency = 1;
    }

    public string Name => "vips";

    public (byte[] Bytes, int Width, int Height) EncodeBuffer(byte[] source, Variant variant)
    {
        using var image = Flatten(Image.ThumbnailBuffer(source, variant.Width, height: MaxCoordinate, size: Enums.Size.Down), variant);
        return (image.WriteToBuffer(SaveOptions(variant)), image.Width, image.Height);
    }

    public Encoded EncodeStream(string input, Variant variant, string folder, string name)
    {
        using var file = Pipelines.OpenSource(input);
        using var image = Flatten(Image.ThumbnailStream(file, variant.Width, height: MaxCoordinate, size: Enums.Size.Down), variant);
        using var target = new HashingFileStream(folder);
        image.WriteToStream(target, SaveOptions(variant));
        var (path, length) = target.Complete(hash => Naming.FileName(name, image.Width, hash, variant.Format));
        return new Encoded(path, image.Width, image.Height, length);
    }

    public byte[] RasterizeSvg(string svg, int width, int height)
    {
        using var image = Image.ThumbnailBuffer(Encoding.UTF8.GetBytes(svg), width, height: height, size: Enums.Size.Force);
        return image.WriteToBuffer(".png[keep=none]");
    }

    private static Image Flatten(Image image, Variant variant)
    {
        if (variant.Format != "jpeg" || !image.HasAlpha())
        {
            return image;
        }

        var flattened = image.Flatten(background: [255, 255, 255]);
        image.Dispose();
        return flattened;
    }

    private static string SaveOptions(Variant variant) => $".{(variant.Format == "jpeg" ? "jpg" : variant.Format)}[Q={variant.Quality},keep=none]";
}

/// <summary>
/// SkiaSharp: <c>SKCodec</c> decodes at the closest smaller scale it supports (JPEG: 1/2, 1/4, 1/8; PNG: none), to sRGB;
/// the rest of the reduction is a draw with mipmaps (a reduction of 2 or more, which a cubic alone would alias) or a
/// Mitchell cubic; WebP and JPEG encoders at the same quality, without metadata (no color space on the output).
/// </summary>
internal sealed class SkiaPipeline : IPipeline
{
    public string Name => "skia";

    public (byte[] Bytes, int Width, int Height) EncodeBuffer(byte[] source, Variant variant)
    {
        // The source bytes as SKData without a copy, as Douze would hand its array over.
        var handle = GCHandle.Alloc(source, GCHandleType.Pinned);
        using var data = SKData.Create(handle.AddrOfPinnedObject(), source.Length, (_, _) => handle.Free());
        using var codec = SKCodec.Create(data) ?? throw new InvalidOperationException("not an image");
        using var bitmap = Resize(codec, variant);
        using var pixmap = bitmap.PeekPixels();
        using var encoded = new SKDynamicMemoryWStream();
        Encode(pixmap, encoded, variant);
        using var bytes = encoded.DetachAsData();
        return (bytes.ToArray(), bitmap.Width, bitmap.Height);
    }

    public Encoded EncodeStream(string input, Variant variant, string folder, string name)
    {
        using var file = Pipelines.OpenSource(input);
        using var codec = SKCodec.Create(file) ?? throw new InvalidOperationException("not an image");
        using var bitmap = Resize(codec, variant);
        using var pixmap = bitmap.PeekPixels();
        using var target = new HashingFileStream(folder);

        using (var stream = new SKManagedWStream(target))
        {
            Encode(pixmap, stream, variant);
        }

        var (path, length) = target.Complete(hash => Naming.FileName(name, bitmap.Width, hash, variant.Format));
        return new Encoded(path, bitmap.Width, bitmap.Height, length);
    }

    public byte[] RasterizeSvg(string svg, int width, int height)
    {
        using var document = new SKSvg();
        var picture = document.FromSvg(svg) ?? throw new InvalidOperationException("cannot read the SVG");
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(width / picture.CullRect.Width, height / picture.CullRect.Height);
        canvas.DrawPicture(picture);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static SKBitmap Resize(SKCodec codec, Variant variant)
    {
        var info = codec.Info;

        // Never wider than the source, the height from the aspect ratio.
        var width = Math.Min(variant.Width, info.Width);
        var height = Math.Max(1, (int)Math.Round((double)info.Height * width / info.Width));

        // Shrink on load: the smallest scale the codec decodes at (JPEG: n/8) that is not smaller than the target.
        var scaled = info.Size;

        for (var eighths = 1; eighths < 8; eighths++)
        {
            var candidate = codec.GetScaledDimensions(eighths / 8f);

            if (candidate.Width >= width && candidate.Height >= height)
            {
                scaled = candidate;
                break;
            }
        }

        var decodedInfo = new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        using var decoded = new SKBitmap(decodedInfo);
        var result = codec.GetPixels(decodedInfo, decoded.GetPixels());

        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            throw new InvalidOperationException($"cannot decode: {result}");
        }

        if (scaled.Width == width && scaled.Height == height && variant.Format != "jpeg")
        {
            return decoded.Copy();
        }

        var output = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));

        using (var canvas = new SKCanvas(output))
        using (var pixels = decoded.PeekPixels())
        using (var image = SKImage.FromPixels(pixels))
        {
            canvas.Clear(variant.Format == "jpeg" ? SKColors.White : SKColors.Transparent);
            var sampling = scaled.Width >= 2 * width
                ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
                : new SKSamplingOptions(SKCubicResampler.Mitchell);
            canvas.DrawImage(image, new SKRect(0, 0, width, height), sampling);
        }

        return output;
    }

    private static void Encode(SKPixmap pixmap, SKWStream stream, Variant variant)
    {
        var encoded = variant.Format switch
        {
            "webp" => pixmap.Encode(stream, new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, variant.Quality)),
            "jpeg" => pixmap.Encode(stream, new SKJpegEncoderOptions(variant.Quality, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore)),
            "png" => pixmap.Encode(stream, SKPngEncoderOptions.Default),
            _ => throw new ArgumentException($"unknown format '{variant.Format}'"),
        };

        if (!encoded)
        {
            throw new InvalidOperationException($"cannot encode {variant.Format}");
        }
    }
}
