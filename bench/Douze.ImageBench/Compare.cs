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

            using var imageA = SKBitmap.Decode(pathA);
            using var imageB = SKBitmap.Decode(pathB);

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

    private static double Psnr(SKBitmap a, SKBitmap b)
    {
        double sum = 0;
        long count = 0;

        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                var (ra, ga, ba) = OnWhite(a.GetPixel(x, y));
                var (rb, gb, bb) = OnWhite(b.GetPixel(x, y));
                sum += (ra - rb) * (ra - rb) + (ga - gb) * (ga - gb) + (ba - bb) * (ba - bb);
                count += 3;
            }
        }

        var mse = sum / count;
        return mse == 0 ? 99 : 10 * Math.Log10(255 * 255 / mse);
    }

    private static (double R, double G, double B) OnWhite(SKColor color)
    {
        var alpha = color.Alpha / 255.0;
        return (color.Red * alpha + 255 * (1 - alpha), color.Green * alpha + 255 * (1 - alpha), color.Blue * alpha + 255 * (1 - alpha));
    }

    [GeneratedRegex(@"\.[0-9a-f]{8}\.")]
    private static partial Regex HashPart();
}
