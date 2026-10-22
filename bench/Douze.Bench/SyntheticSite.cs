using System.Globalization;
using System.Text;

namespace Douze.Bench;

/// <summary>
/// The synthetic large site of the plan's section 9.1: N Markdown pages of about 4 KB with front matter (title, date,
/// 2 tags out of 20), a layout chain of 2 levels, 3 includes used by the layout, 5 site data files, a nav include that
/// loops over 50 items of <c>collections.all</c>, a page paginated over a 2,000-item data file (size 20) and 200 static
/// assets. The content only depends on N, so two generations are identical.
/// </summary>
internal static class SyntheticSite
{
    private const int TagCount = 20;
    private const int PagesPerSection = 500;
    private const int ProductCount = 2_000;

    private static readonly string[] Words =
    [
        "action", "build", "cache", "deploy", "engine", "file", "graph", "host", "input", "join", "kernel", "layout",
        "markdown", "node", "output", "page", "query", "render", "step", "template", "unit", "value", "worker",
        "yaml", "zone", "the", "a", "of", "to", "and", "in", "with", "for", "is", "on", "that", "by", "this", "from",
        "site", "data", "scope", "content", "liquid", "include", "collection", "tag", "date", "path", "stream",
    ];

    /// <summary>
    /// Writes the site to <paramref name="outDir"/>. <paramref name="nav"/> is how the nav include picks its 50 items:
    /// <c>limit</c> (<c>for p in collections.all limit: 50</c>, the plan's site), <c>index</c>
    /// (<c>collections.all[i]</c> for i in 0..49) or <c>none</c> (no nav).
    /// </summary>
    public static void Generate(string outDir, int pageCount, string nav)
    {
        if (nav is not ("limit" or "index" or "none"))
        {
            throw new BenchException($"--nav must be 'limit', 'index' or 'none', not '{nav}'");
        }

        var root = Path.GetFullPath(outDir);

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        WriteIncludes(root, nav);
        WriteData(root);
        WriteContent(root, pageCount);
        WriteAssets(root);

        Console.WriteLine($"synthetic site with {pageCount} pages (nav: {nav}) written to {root}");
    }

    private static void WriteIncludes(string root, string nav)
    {
        Write(root, "_includes/base.html", """
            <!doctype html>
            <html lang="en">
              <head>
                <meta charset="utf-8">
                <title>{{ title }} - {{ site.title }}</title>
                <link rel="stylesheet" href="/assets/css/style-000.css">
              </head>
              <body>
                {% include 'header' %}
                <div class="main">
                  {% include 'nav' %}
                  <main>{{ content | raw }}</main>
                </div>
                {% include 'footer' %}
              </body>
            </html>
            """);

        Write(root, "_includes/post.html", """
            ---
            layout: base.html
            ---
            <article>
              <header>
                <p class="date">{{ date | date: "%Y-%m-%d" }}</p>
                <ul class="tags">{% for tag in tags %}<li><a href="/tags/{{ tag }}/">{{ tag }}</a></li>{% endfor %}</ul>
              </header>
              {{ content | raw }}
            </article>
            """);

        Write(root, "_includes/header.liquid", """
            <header class="site">
              <a href="{{ site.url }}">{{ site.title }}</a>
              <ul>{% for item in menu %}<li><a href="{{ item.url }}">{{ item.title }}</a></li>{% endfor %}</ul>
            </header>
            """);

        var loop = nav switch
        {
            "limit" => "{%- for p in collections.all limit: 50 %}",
            "index" => "{%- for i in (0..49) %}{% assign p = collections.all[i] %}",
            _ => null,
        };

        Write(root, "_includes/nav.liquid", loop == null ? "<nav></nav>\n" : $$$"""
            <nav>
              <ul>
              {{{loop}}}
                <li><a href="{{ p.data.url }}">{{ p.data.title }}</a></li>
              {%- endfor %}
              </ul>
            </nav>
            """);

        Write(root, "_includes/footer.liquid", """
            <footer>
              <ul>{% for author in authors %}<li>{{ author.name }} ({{ author.role }})</li>{% endfor %}</ul>
              <p>{{ site.copyright }}</p>
            </footer>
            """);
    }

    private static void WriteData(string root)
    {
        Write(root, "_data/layout.json", "\"post.html\"");

        Write(root, "_data/site.json", """
            {
              "title": "Synthetic",
              "url": "https://example.org/",
              "copyright": "Copyright the synthetic site authors"
            }
            """);

        Write(root, "_data/authors.yml", """
            - name: Ada
              role: editor
            - name: Grace
              role: author
            - name: Linus
              role: reviewer
            """);

        var menu = new StringBuilder("[\n");

        for (var i = 0; i < 10; i++)
        {
            menu.Append(CultureInfo.InvariantCulture, $"  {{ \"title\": \"Section {i}\", \"url\": \"/docs/section-{i:D2}/\" }}{(i < 9 ? "," : "")}\n");
        }

        Write(root, "_data/menu.json", menu.Append(']').ToString());

        var products = new StringBuilder("[\n");
        var random = new Random(ProductCount);

        for (var i = 0; i < ProductCount; i++)
        {
            products.Append(CultureInfo.InvariantCulture, $"  {{ \"id\": {i}, \"name\": \"Product {i}\", \"price\": {random.Next(100, 10_000) / 100m}, \"category\": \"{Words[i % Words.Length]}\" }}{(i < ProductCount - 1 ? "," : "")}\n");
        }

        Write(root, "_data/products.json", products.Append(']').ToString());
    }

    private static void WriteContent(string root, int pageCount)
    {
        Write(root, "_content/index.md", """
            ---
            title: Home
            ---
            # {{ title }}

            The synthetic site has {{ collections.all.size }} pages.
            """);

        Write(root, "_content/products.html", """
            ---
            title: Products
            pagination:
              data: products
              size: 20
              alias: items
            ---
            <h1>{{ title }} {{ pagination.pageNumber }}</h1>
            <ul>
            {%- for p in items %}
              <li>{{ p.id }}: {{ p.name }} ({{ p.category }}) {{ p.price }}</li>
            {%- endfor %}
            </ul>
            {% if pagination.page.next %}<a href="{{ pagination.page.next.href }}">next</a>{% endif %}
            """);

        Parallel.For(0, pageCount, i =>
        {
            var section = i / PagesPerSection;
            Write(root, $"_content/docs/section-{section:D2}/page-{i:D5}.md", Page(i, pageCount));
        });
    }

    private static string Page(int index, int pageCount)
    {
        var random = new Random(index);
        var tag1 = random.Next(TagCount);
        var tag2 = (tag1 + 1 + random.Next(TagCount - 1)) % TagCount;
        var date = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(37.0 * index);

        var builder = new StringBuilder(4_500);
        builder.Append(CultureInfo.InvariantCulture, $"""
            ---
            title: Page {index}
            date: {date:yyyy-MM-ddTHH:mm:ssZ}
            tags: [tag-{tag1:D2}, tag-{tag2:D2}]
            ---
            # {"{{ title }}"}


            """);

        for (var section = 0; section < 4; section++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"## {Sentence(random, 4)}\n\n");

            for (var paragraph = 0; paragraph < 2; paragraph++)
            {
                builder.Append(Sentence(random, 70)).Append(" See [page ");
                var other = random.Next(pageCount);
                builder.Append(CultureInfo.InvariantCulture, $"{other}](/docs/section-{other / PagesPerSection:D2}/page-{other:D5}/) and **{Words[random.Next(Words.Length)]}**.\n\n");
            }

            for (var item = 0; item < 3; item++)
            {
                builder.Append("- ").Append(Sentence(random, 8)).Append('\n');
            }

            builder.Append("\n```text\n").Append(Sentence(random, 12)).Append("\n```\n\n");
        }

        return builder.ToString();
    }

    private static string Sentence(Random random, int words)
    {
        var builder = new StringBuilder();

        for (var i = 0; i < words; i++)
        {
            builder.Append(i == 0 ? "" : " ").Append(Words[random.Next(Words.Length)]);
        }

        builder[0] = char.ToUpperInvariant(builder[0]);
        return builder.Append('.').ToString();
    }

    private static void WriteAssets(string root)
    {
        // 70 stylesheets, 70 scripts and 60 images: 200 files no step claims.
        for (var i = 0; i < 200; i++)
        {
            var random = new Random(-1 - i);

            if (i < 70)
            {
                var css = new StringBuilder();

                for (var rule = 0; rule < 40; rule++)
                {
                    css.Append(CultureInfo.InvariantCulture, $".c{i}-{rule} {{ color: #{random.Next(0x1000000):x6}; margin: {random.Next(20)}px; }}\n");
                }

                Write(root, $"_content/assets/css/style-{i:D3}.css", css.ToString());
            }
            else if (i < 140)
            {
                var js = new StringBuilder();

                for (var line = 0; line < 40; line++)
                {
                    js.Append(CultureInfo.InvariantCulture, $"export function f{i}_{line}(x) {{ return x * {random.Next(1000)} + {random.Next(1000)}; }}\n");
                }

                Write(root, $"_content/assets/js/script-{i:D3}.js", js.ToString());
            }
            else
            {
                // A PNG signature followed by noise: assets are copied, never decoded.
                var bytes = new byte[8 * 1024];
                random.NextBytes(bytes);
                ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
                signature.CopyTo(bytes);
                var path = Path.Combine(root, $"_content/assets/img/image-{i:D3}.png");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
            }
        }
    }

    internal static void Write(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.ReplaceLineEndings("\n"));
    }
}
