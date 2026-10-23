using System.Globalization;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Douze.ImageBench;

/// <summary>
/// Compares two output folders of <c>variants</c>, file by file (matched by name without the hash): dimensions, size,
/// and the PSNR of their pixels (decoded by SkiaSharp, composited on white), which tells whether they look the same:
/// above 40 dB the difference is not visible, 30 to 40 dB is resampling and encoder noise.
/// </summary>
internal static partial class Compare
{
    public static int Run(string a, string b)
    {
        var left = Index(a);
        var right = Index(b);
        int pairs = 0, sizeMismatch = 0;
        long bytesA = 0, bytesB = 0;
        var psnrs = new List<(double Psnr, string Key)>();

        foreach (var (key, pathA) in left.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (!right.TryGetValue(key, out var pathB))
            {
                continue;
            }

            pairs++;
            bytesA += new FileInfo(pathA).Length;
            bytesB += new FileInfo(pathB).Length;

            using var imageA = Decode(pathA);
            using var imageB = Decode(pathB);

            if (imageA.Width != imageB.Width || imageA.Height != imageB.Height)
            {
                sizeMismatch++;
                Console.WriteLine($"  {key}: {imageA.Width}x{imageA.Height} against {imageB.Width}x{imageB.Height}");
                continue;
            }

            psnrs.Add((Psnr(imageA, imageB), key));
        }

        psnrs.Sort();
        var median = psnrs.Count > 0 ? psnrs[psnrs.Count / 2].Psnr : 0;

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{pairs} pairs ({left.Count} and {right.Count} files), {sizeMismatch} with other dimensions, bytes {bytesA / 1024.0 / 1024:F1} MB against {bytesB / 1024.0 / 1024:F1} MB ({(double)bytesB / Math.Max(1, bytesA):P0}), PSNR median {median:F1} dB, lowest {string.Join(", ", psnrs.Take(3).Select(x => $"{x.Psnr:F1} dB ({x.Key})"))}"));
        return 0;
    }

    private static Dictionary<string, string> Index(string folder)
    {
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(x => !Path.GetFileName(x).StartsWith('.'))
            .ToDictionary(x => HashPart().Replace(Path.GetRelativePath(folder, x), "."), x => x, StringComparer.Ordinal);
    }

    /// <summary>
    /// The pixels of an image as unpremultiplied RGBA, 8 bits per channel, whatever its format.
    /// </summary>
    private static SKBitmap Decode(string path)
    {
        using var codec = SKCodec.Create(path) ?? throw new InvalidOperationException($"cannot read '{path}'");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        codec.GetPixels(info, bitmap.GetPixels());
        return bitmap;
    }

    // Over the raw pixels: GetPixel per pixel took minutes over a site's variants.
    private static double Psnr(SKBitmap a, SKBitmap b)
    {
        var pixelsA = a.GetPixelSpan();
        var pixelsB = b.GetPixelSpan();
        double sum = 0;

        for (var i = 0; i < pixelsA.Length; i += 4)
        {
            double alphaA = pixelsA[i + 3] / 255.0, alphaB = pixelsB[i + 3] / 255.0;

            for (var c = 0; c < 3; c++)
            {
                var difference = OnWhite(pixelsA[i + c], alphaA) - OnWhite(pixelsB[i + c], alphaB);
                sum += difference * difference;
            }
        }

        var mse = sum / (pixelsA.Length / 4 * 3);
        return mse == 0 ? 99 : 10 * Math.Log10(255 * 255 / mse);
    }

    private static double OnWhite(byte channel, double alpha) => channel * alpha + 255 * (1 - alpha);

    [GeneratedRegex(@"\.[0-9a-f]{8}\.")]
    private static partial Regex HashPart();
}
