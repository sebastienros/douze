using System.Globalization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace Douze.Bench;

/// <summary>
/// A site of the image features: JPEG photos (1600x1200, gradients with a quarter of every row noise, quality 85, about
/// 1.2 MB each, the set of the first image benchmark) and PNG screenshots (1440x900, flat panels) through the
/// <c>images</c>, <c>image-map</c> and <c>pictures</c> steps (640 and 1280 wide WebP), and Markdown pages that show
/// them, each with a social image (<c>social-image</c>: an SVG card with a gradient, shapes and an inlined PNG logo,
/// and two text blocks with a font file). The content only depends on the counts, so two generations are identical.
/// </summary>
internal static class ImageSite
{
    public static void Generate(string outDir, int photos, int screenshots, int cards, string? font)
    {
        var root = Path.GetFullPath(outDir);

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        font ??= FindFont() ?? throw new BenchException("no font file found; pass --font <file.ttf>");

        for (var i = 0; i < photos; i++)
        {
            WriteImage(root, $"_content/photos/photo-{i:D3}.jpg", Photo(i), new JpegEncoder { Quality = 85 });
        }

        for (var i = 0; i < screenshots; i++)
        {
            WriteImage(root, $"_content/shots/shot-{i:D3}.png", Screenshot(i), new PngEncoder { ColorType = PngColorType.Rgb });
        }

        WriteImage(root, "_includes/og/logo.png", Logo(), new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
        Directory.CreateDirectory(Path.Combine(root, "assets/fonts"));
        File.Copy(font, Path.Combine(root, "assets/fonts/font.ttf"));

        var pages = Math.Max(cards, 1);

        for (var i = 0; i < pages; i++)
        {
            var images = new List<string>();

            if (photos > 0)
            {
                images.Add($"![Photo {i}](../photos/photo-{i % photos:D3}.jpg)");
            }

            if (screenshots > 0)
            {
                images.Add($"![Screenshot {i}](../shots/shot-{i % screenshots:D3}.png)");
            }

            SyntheticSite.Write(root, $"_content/posts/post-{i:D3}.md", $"""
                ---
                title: "Post {i}: a page about images and social cards"
                description: "The description of post {i}, long enough to wrap on two lines of the card's second text block."
                og: {(cards > 0 ? "true" : "false")}
                layout: page.html
                ---
                # Post {i}

                {string.Join("\n\n", images)}

                """);
        }

        SyntheticSite.Write(root, "_includes/page.html", """
            <!doctype html>
            <html lang="en">
              <head>
                <meta charset="utf-8">
                <title>{{ title }}</title>
                {% if og %}<meta property="og:image" content="{{ page | social_image_url: 'og/{{ title | slug }}.png' }}">{% endif %}
              </head>
              <body>{{ content }}</body>
            </html>
            """);

        SyntheticSite.Write(root, "_includes/og/card.svg.liquid", """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="1200" height="630" viewBox="0 0 1200 630">
              <defs>
                <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
                  <stop offset="0" stop-color="#1b1f3b"/>
                  <stop offset="1" stop-color="#3b1f4f"/>
                </linearGradient>
                <radialGradient id="glow" cx="0.85" cy="0.15" r="0.6">
                  <stop offset="0" stop-color="#ff7a59" stop-opacity="0.55"/>
                  <stop offset="1" stop-color="#ff7a59" stop-opacity="0"/>
                </radialGradient>
              </defs>
              <rect width="1200" height="630" fill="url(#bg)"/>
              <rect width="1200" height="630" fill="url(#glow)"/>
              <circle cx="1080" cy="540" r="160" fill="none" stroke="#ffffff" stroke-opacity="0.12" stroke-width="24"/>
              <path d="M0 600 C 300 520, 600 680, 1200 560 L 1200 630 L 0 630 Z" fill="#ffffff" fill-opacity="0.06"/>
              <rect x="80" y="64" width="220" height="44" rx="22" fill="#ffffff" fill-opacity="0.14"/>
              <image xlink:href="logo.png" x="1040" y="56" width="96" height="96"/>
            </svg>
            """);

        var steps = new List<string>
        {
            """  pages: { inputs: "_content/**/*.md", uses: frontmatter }""",
            """  permalink: { needs: pages, uses: permalink }""",
        };

        if (cards > 0)
        {
            steps.Add("""
                  og:
                    needs: permalink
                    uses: social-image
                    with:
                      template: og/card.svg.liquid
                      output: "og/{{ title | slug }}.png"
                      skip: "og == false"
                      text:
                        - value: "{{ title }}"
                          font: "sans bold 60"
                          fontfile: assets/fonts/font.ttf
                          x: 80
                          y: 150
                          width: 1040
                          height: 240
                          color: "#ffffff"
                        - value: "{{ description }}"
                          font: "sans 30"
                          fontfile: assets/fonts/font.ttf
                          x: 80
                          y: 420
                          width: 1040
                          height: 120
                          color: "#c8c8d0"
                """.TrimEnd());
        }

        steps.Add($$"""  render: { needs: {{(cards > 0 ? "og" : "permalink")}}, uses: liquid }""");
        steps.Add("""  markdown: { needs: render, uses: markdown }""");

        if (photos + screenshots > 0)
        {
            steps.Add("""  images: { inputs: "_content/**/*.{png,jpg}", uses: images, with: { widths: [640, 1280], formats: [webp] } }""");
            steps.Add("""  image-map: { needs: images, uses: image-map }""");
            steps.Add("""  pictures: { needs: [markdown, image-map], uses: pictures }""");
            steps.Add("""  layout: { needs: pictures, uses: layout }""");
            steps.Add("""  write: { needs: [layout, images], uses: write }""");
        }
        else
        {
            steps.Add("""  layout: { needs: markdown, uses: layout }""");
            steps.Add("""  write: { needs: layout, uses: write }""");
        }

        SyntheticSite.Write(root, "douze.yml", $"steps:\n{string.Join("\n", steps)}\n");

        Console.WriteLine($"image site with {photos} photos, {screenshots} screenshots and {cards} social cards written to {root}");
    }

    private static string? FindFont()
    {
        string[] candidates =
        [
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/dejavu/DejaVuSans.ttf",
            "/System/Library/Fonts/Supplemental/Arial.ttf",
            "/Library/Fonts/Arial.ttf",
            @"C:\Windows\Fonts\arial.ttf",
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// A photo-like JPEG source: a diagonal gradient whose hue depends on the index, and noise on a quarter of every row
    /// (what keeps it from compressing to nothing).
    /// </summary>
    private static Image<Rgb24> Photo(int index)
    {
        var random = new Random(index);
        var image = new Image<Rgb24>(1600, 1200);
        var (hr, hg, hb) = (random.Next(256), random.Next(256), random.Next(256));

        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                var noiseStart = random.Next(row.Length * 3 / 4);

                for (var x = 0; x < row.Length; x++)
                {
                    var t = (x + y) / (float)(row.Length + rows.Height);
                    var pixel = new Rgb24((byte)(hr * (1 - t) + 255 * t * 0.6f), (byte)(hg * (1 - t) + 128 * t), (byte)(hb * t + 40 * (1 - t)));

                    if (x >= noiseStart && x < noiseStart + row.Length / 4)
                    {
                        var noise = random.Next(-40, 41);
                        pixel = new Rgb24(Clamp(pixel.R + noise), Clamp(pixel.G + noise), Clamp(pixel.B + noise));
                    }

                    row[x] = pixel;
                }
            }
        });

        return image;
    }

    /// <summary>
    /// A screenshot-like PNG source: a title bar, a sidebar, panels of flat colors and lines of "text" (thin bars).
    /// </summary>
    private static Image<Rgb24> Screenshot(int index)
    {
        var random = new Random(10_000 + index);
        var image = new Image<Rgb24>(1440, 900, new Rgb24(245, 246, 248));
        var accent = new Rgb24((byte)random.Next(40, 200), (byte)random.Next(40, 200), (byte)random.Next(120, 240));

        image.ProcessPixelRows(rows =>
        {
            static void Fill(SixLabors.ImageSharp.PixelAccessor<Rgb24> rows, int x0, int y0, int width, int height, Rgb24 color)
            {
                for (var y = y0; y < Math.Min(rows.Height, y0 + height); y++)
                {
                    rows.GetRowSpan(y).Slice(x0, Math.Min(width, rows.Width - x0)).Fill(color);
                }
            }

            Fill(rows, 0, 0, 1440, 48, accent);
            Fill(rows, 0, 48, 260, 852, new Rgb24(30, 32, 40));

            for (var item = 0; item < 18; item++)
            {
                Fill(rows, 24, 80 + item * 40, 120 + random.Next(100), 10, new Rgb24(150, 155, 170));
            }

            for (var panel = 0; panel < 6; panel++)
            {
                var (px, py) = (300 + panel % 3 * 370, 90 + panel / 3 * 400);
                Fill(rows, px, py, 340, 370, new Rgb24(255, 255, 255));
                Fill(rows, px, py, 340, 4, accent);

                for (var line = 0; line < 14; line++)
                {
                    Fill(rows, px + 20, py + 30 + line * 22, 120 + random.Next(180), 8, new Rgb24(90, 95, 110));
                }
            }
        });

        return image;
    }

    private static Image<Rgba32> Logo()
    {
        var image = new Image<Rgba32>(96, 96);

        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);

                for (var x = 0; x < row.Length; x++)
                {
                    var distance = Math.Sqrt((x - 47.5) * (x - 47.5) + (y - 47.5) * (y - 47.5));
                    row[x] = distance < 44 ? new Rgba32(255, 122, 89, (byte)Math.Clamp((44 - distance) * 255, 0, 255)) : default;
                }
            }
        });

        return image;
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    private static void WriteImage(string root, string relativePath, Image image, SixLabors.ImageSharp.Formats.ImageEncoder encoder)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using (image)
        {
            image.Save(path, encoder);
        }
    }
}
