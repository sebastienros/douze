using System.Text;
using Douze.FileSystems;
using Douze.Tests.Workflow.Watch;
using Douze.Workflow;
using Douze.Workflow.Watch;
using Xunit;

namespace Douze.Tests.Workflow.Cache;

/// <summary>
/// The key test of incremental builds: for random sequences of edits of a generated site (pages modified, added,
/// deleted and renamed; layouts, includes, site and folder data, a paginated page), the output folder of a watch session
/// and the output folder of builds from the disk cache are the same, file for file, as the output of a clean build. A
/// failure prints the seed and the edits; a seed runs alone as its own theory case.
/// </summary>
public class IncrementalEqualsCleanTests : CacheTestBase
{
    private const int Pages = 200;
    private const int Edits = 12;

    private static readonly string[] Tags = ["red", "green", "blue", "cyan", "pink"];
    private static readonly string[] Sections = ["blog", "docs", "news"];

    private readonly List<FPath> _pages = [];
    private int _nextPage = Pages;

    // Every file written gets a time of its own, older than the build: the cache trusts the stats of such files, which
    // the source snapshot of the watch session must then keep right.
    private DateTime _clock = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static TheoryData<int> Seeds => [.. Enumerable.Range(1, 25)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task IncrementalBuildEqualsCleanBuild(int seed)
    {
        var random = new Random(seed);
        var history = new List<string>();

        await WriteSiteAsync(random);

        using var session = new TestSession(Source);
        await session.BuildSuccessfullyAsync();
        await CompareAsync(seed, history, session);

        var replayed = 0;

        for (var i = 0; i < Edits; i++)
        {
            var changes = new ChangeSet();
            history.Add(await EditAsync(random, changes));

            var rebuilt = await session.BuildSuccessfullyAsync(changes);
            replayed += rebuilt.Build.UnitsReplayed;

            await CompareAsync(seed, history, session);
        }

        // The builds after an edit did replay most units.
        Assert.True(replayed > Edits * Pages, $"seed {seed}: {replayed} units replayed");
    }

    /// <summary>
    /// Builds from the disk cache and cleanly, and compares both output folders and the watch session's with the clean
    /// one.
    /// </summary>
    private async Task CompareAsync(int seed, List<string> history, TestSession session)
    {
        await BuildCachedAsync();

        var cached = Output;
        Output = new MemoryFileSystem();
        Site = null;
        await BuildCachedAsync(cache: false);
        var clean = Output;
        Output = cached;
        Site = null;

        var message = $"seed {seed}, edits: {string.Join(" | ", history)}";
        var expected = await TestSession.SnapshotAsync(clean);

        await AssertSameAsync("cache", cached);
        await AssertSameAsync("watch", session.Output);

        async Task AssertSameAsync(string kind, MemoryFileSystem output)
        {
            var actual = await TestSession.SnapshotAsync(output);
            var missing = expected.Keys.Except(actual.Keys).ToArray();
            var extra = actual.Keys.Except(expected.Keys).ToArray();

            Assert.True(missing.Length == 0 && extra.Length == 0, $"{message}\n{kind}: missing {string.Join(", ", missing)}; extra {string.Join(", ", extra)}");

            foreach (var (path, hash) in expected)
            {
                if (actual[path] != hash)
                {
                    Assert.Fail($"{message}\n{kind}: {path} differs:\nclean: {await clean.ReadAllTextAsync(path)}\n{kind}: {await output.ReadAllTextAsync(path)}");
                }
            }
        }
    }

    private async Task WriteSiteAsync(Random random)
    {
        await Source.WriteTextFileAsync("/_data/site.yml", "title: Douze\nauthor: me");
        await Source.WriteTextFileAsync("/_data/menu.yml", "- Home\n- About");
        await Source.WriteTextFileAsync("/_data/layout.json", "\"layout.html\"");
        await Source.WriteTextFileAsync("/_data/products.json", Products(0));
        await Source.WriteTextFileAsync("/_data/authors.yml", "me: Me\nyou: You");
        await Source.WriteTextFileAsync("/_data/colors.json", "{ \"main\": \"red\" }");
        await Source.WriteTextFileAsync("/_content/blog/blog.yml", "layout: post.html\nsection: Blog");
        await Source.WriteTextFileAsync("/_content/docs/docs.yml", "layout: doc.html");
        await Source.WriteTextFileAsync("/_includes/layout.html", Layout(0));
        await Source.WriteTextFileAsync("/_includes/post.html", Post(0));
        await Source.WriteTextFileAsync("/_includes/doc.html", Doc(0));
        await Source.WriteTextFileAsync("/_includes/nav.liquid", Nav(0));
        await Source.WriteTextFileAsync("/_includes/tags.liquid", "<ul>{% for tag in tags %}<li>{{ tag }}</li>{% endfor %}</ul>");
        await Source.WriteTextFileAsync("/_includes/footer.liquid", Footer(0));
        await Source.WriteTextFileAsync("/_includes/meta.html", "<meta name=\"author\" content=\"{{ authors.me }}\">");
        await Source.WriteTextFileAsync("/_content/index.md", "---\ntitle: Home\ndate: 2019-01-01\n---\n# {{ site.title }}\n\n{{ collections.all | size }} pages, {% for p in collections.red %}{{ p.data.title }} {% endfor %} {{ extra.value }}");
        await Source.WriteTextFileAsync("/_content/products.md", ProductsPage(5));
        await Source.WriteTextFileAsync("/_content/latest.md", "---\ntitle: Latest\ndate: 2019-01-03\npagination:\n  data: collections.blue\n  size: 3\n---\n{% for p in pagination.items %}{{ p.data.title }} {% endfor %}");
        await Source.WriteTextFileAsync("/_content/styles.css", "body { color: red; }");

        for (var i = 0; i < Pages; i++)
        {
            var path = PagePath(i, Sections[i % Sections.Length]);
            _pages.Add(path);
            await Source.WriteTextFileAsync(path, Page(i, random, 0));
        }

        foreach (var entry in await Source.EnumerateFileSystemEntriesAsync(FPath.Root, recursive: true, filter: null))
        {
            if (!entry.IsDirectory)
            {
                Source.SetLastWriteTimeUtc(entry.Path, Tick());
            }
        }
    }

    private DateTime Tick() => _clock = _clock.AddSeconds(1);

    private static FPath PagePath(int i, string section) => $"/_content/{section}/page-{i:000}.md";

    private static string Page(int i, Random random, int version)
    {
        var tags = Tags.OrderBy(_ => random.Next()).Take(random.Next(0, 3)).ToArray();
        var builder = new StringBuilder();
        builder.Append($"---\ntitle: Page {i} v{version}\ndate: 2020-{1 + i % 12:00}-{1 + i % 28:00}\n");

        if (tags.Length > 0)
        {
            builder.Append($"tags: [{string.Join(", ", tags)}]\n");
        }

        builder.Append($"---\n## Page {i}\n\nBody {version} of {{{{ title }}}} by {{{{ site.author }}}}.");

        if (random.Next(3) == 0)
        {
            builder.Append(" {% for m in menu %}{{ m }}{% endfor %}");
        }

        if (random.Next(4) == 0)
        {
            builder.Append(" {{ colors.main }}");
        }

        return builder.ToString();
    }

    private static string Products(int version) => "[" + string.Join(", ", Enumerable.Range(0, 12).Select(i => $"{{ \"name\": \"P{i}v{(i == version % 12 ? version : 0)}\" }}")) + "]";

    private static string ProductsPage(int size) => $"---\ntitle: Products\ndate: 2019-01-02\npagination:\n  data: products\n  size: {size}\n  alias: page_products\n---\n{{% for p in page_products %}}{{{{ p.name }}}} {{% endfor %}} page {{{{ pagination.pageNumber }}}}";

    private static string Layout(int version) => $"<html><head>{{% include 'meta' %}}<title>{{{{ title }}}} - {{{{ site.title }}}}</title></head><body>{{% include 'nav' %}}<main>{{{{ content }}}}</main>{{% include 'footer' %}} v{version} {{{{ section }}}}</body></html>";

    private static string Post(int version) => $"---\nlayout: layout.html\nsection: Posts\n---\n<article>{{{{ content }}}}{{% include 'tags' %}} post v{version}</article>";

    private static string Doc(int version) => $"---\nlayout: layout.html\n---\n<div class=\"doc\">{{{{ content }}}} doc v{version}</div>";

    private static string Nav(int version) => $"<nav v{version}>{{% for p in collections.all limit: 3 %}}<a href=\"{{{{ p.data.url }}}}\">{{{{ p.data.title }}}}</a>{{% endfor %}}</nav>";

    private static string Footer(int version) => $"<footer>{{{{ site.author }}}} f{version}</footer>";

    /// <summary>
    /// Applies one random edit, records it in <paramref name="changes"/> as the watcher would, and describes it.
    /// </summary>
    private async Task<string> EditAsync(Random random, ChangeSet changes)
    {
        var version = random.Next(1, 1000);

        switch (random.Next(16))
        {
            case 0:
            case 1:
                {
                    // Body and front matter (title, tags, data used) of a page.
                    var path = _pages[random.Next(_pages.Count)];
                    await WriteAsync(path, Page(Number(path), random, version), changes);
                    return $"page {path} v{version}";
                }

            case 2:
                {
                    // The body only, with the same date.
                    var path = _pages[random.Next(_pages.Count)];
                    var date = (await Source.GetFileMetadataAsync(path)).LastWriteTimeUtc;
                    var text = await Source.ReadAllTextAsync(path);
                    await WriteAsync(path, text + $"\n\nMore {version}.", changes);
                    Source.SetLastWriteTimeUtc(path, date);
                    return $"body of {path} v{version}";
                }

            case 3:
                {
                    var i = _nextPage++;
                    var path = PagePath(i, Sections[random.Next(Sections.Length)]);
                    _pages.Add(path);
                    await WriteAsync(path, Page(i, random, version), changes);
                    return $"add {path}";
                }

            case 4:
                {
                    var path = _pages[random.Next(_pages.Count)];
                    _pages.Remove(path);
                    await Source.DeleteFileAsync(path);
                    changes.Add(path, ChangeKind.Deleted);
                    return $"delete {path}";
                }

            case 5:
                {
                    // To another name, maybe in another section.
                    var from = _pages[random.Next(_pages.Count)];
                    var to = PagePath(_nextPage++, Sections[random.Next(Sections.Length)]);
                    var text = await Source.ReadAllTextAsync(from);
                    _pages[_pages.IndexOf(from)] = to;
                    await Source.DeleteFileAsync(from);
                    await Source.WriteTextFileAsync(to, text);
                    Source.SetLastWriteTimeUtc(to, Tick());
                    changes.Rename(from, to);
                    return $"rename {from} to {to}";
                }

            case 6:
                await WriteAsync("/_includes/layout.html", Layout(version), changes);
                return $"layout v{version}";
            case 7:
                if (random.Next(2) == 0)
                {
                    await WriteAsync("/_includes/post.html", Post(version), changes);
                    return $"post layout v{version}";
                }

                await WriteAsync("/_includes/doc.html", Doc(version), changes);
                return $"doc layout v{version}";
            case 8:
                if (random.Next(2) == 0)
                {
                    await WriteAsync("/_includes/nav.liquid", Nav(version), changes);
                    return $"nav v{version}";
                }

                await WriteAsync("/_includes/footer.liquid", Footer(version), changes);
                return $"footer v{version}";
            case 9:
                await WriteAsync("/_data/products.json", Products(version), changes);
                return $"products v{version}";
            case 10:
                await WriteAsync(random.Next(2) == 0 ? "/_data/site.yml" : "/_data/menu.yml", random.Next(2) == 0 ? $"title: Douze {version}\nauthor: me" : $"- Home\n- About {version}", changes);
                return $"site data v{version}";
            case 11:
                if (random.Next(2) == 0)
                {
                    await WriteAsync("/_data/colors.json", $"{{ \"main\": \"c{version}\" }}", changes);
                    return $"colors v{version}";
                }

                await WriteAsync("/_data/authors.yml", $"me: Me {version}\nyou: You", changes);
                return $"authors v{version}";
            case 12:
                {
                    // A data file added, or deleted when it is there.
                    var path = (FPath)"/_data/extra.yml";

                    if ((await Source.GetFileEntryAsync(path)).Exists)
                    {
                        await Source.DeleteFileAsync(path);
                        changes.Add(path, ChangeKind.Deleted);
                        return "delete extra data";
                    }

                    await WriteAsync(path, $"value: extra {version}", changes);
                    return $"add extra data v{version}";
                }

            case 13:
                {
                    // Folder data added to a section, or deleted when it is there.
                    var path = (FPath)"/_content/news/news.yml";

                    if ((await Source.GetFileEntryAsync(path)).Exists)
                    {
                        await Source.DeleteFileAsync(path);
                        changes.Add(path, ChangeKind.Deleted);
                        return "delete news folder data";
                    }

                    await WriteAsync(path, $"section: News {version}", changes);
                    return $"add news folder data v{version}";
                }

            case 14:
                await WriteAsync("/_content/blog/blog.yml", random.Next(2) == 0 ? $"layout: post.html\nsection: Blog {version}" : $"section: Blog {version}", changes);
                return $"blog folder data v{version}";
            default:
                {
                    // Fewer or more pages.
                    var size = random.Next(2, 7);
                    await WriteAsync("/_content/products.md", ProductsPage(size), changes);
                    return $"products page size {size}";
                }
        }
    }

    private async Task WriteAsync(FPath path, string content, ChangeSet changes)
    {
        var exists = (await Source.GetFileEntryAsync(path)).Exists;
        await Source.WriteTextFileAsync(path, content);
        Source.SetLastWriteTimeUtc(path, Tick());
        changes.Add(path, exists ? ChangeKind.Modified : ChangeKind.Added);
    }

    private static int Number(FPath path) => int.Parse(path.FullName[(path.FullName.LastIndexOf('-') + 1)..^3], System.Globalization.CultureInfo.InvariantCulture);
}
