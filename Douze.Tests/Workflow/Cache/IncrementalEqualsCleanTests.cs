using System.Text;
using Douze.FileSystems;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow.Cache;

/// <summary>
/// For random sequences of edits of a generated site, a build from the disk cache gives the same output as a clean
/// build. Edits modify files only: adding, deleting and renaming are phase 6's (stale outputs are not removed yet).
/// </summary>
public class IncrementalEqualsCleanTests : CacheTestBase
{
    private const int Pages = 40;
    private const int Edits = 12;

    private static readonly string[] Tags = ["red", "green", "blue", "cyan", "pink"];
    private static readonly string[] Sections = ["blog", "docs", "news"];

    public static TheoryData<int> Seeds => [.. Enumerable.Range(1, 25)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task IncrementalBuildEqualsCleanBuild(int seed)
    {
        var random = new Random(seed);
        var history = new List<string>();

        await WriteSiteAsync(random);
        await CompareAsync(seed, history);

        var replayed = 0;

        for (var i = 0; i < Edits; i++)
        {
            history.Add(await EditAsync(random));
            replayed += await CompareAsync(seed, history);
        }

        // The builds after an edit did use the cache.
        Assert.True(replayed > Edits * Pages, $"seed {seed}: {replayed} units replayed");
    }

    /// <summary>
    /// Builds incrementally and cleanly, compares the outputs, and returns the number of units replayed.
    /// </summary>
    private async Task<int> CompareAsync(int seed, List<string> history)
    {
        var incremental = await BuildCachedAsync();

        var output = Output;
        Output = new MemoryFileSystem();
        Site = null;
        var clean = await BuildCachedAsync(cache: false);
        var cleanOutput = Output;
        Output = output;
        Site = null;

        var message = $"seed {seed}, edits: {string.Join(" | ", history)}";

        Assert.True(clean.Outputs.SequenceEqual(incremental.Outputs), $"{message}\nclean:       {string.Join(", ", clean.Outputs)}\nincremental: {string.Join(", ", incremental.Outputs)}");

        foreach (var file in clean.Outputs)
        {
            var expected = await cleanOutput.ReadAllTextAsync(file.Path);
            var actual = await output.ReadAllTextAsync(file.Path);
            Assert.True(expected == actual, $"{message}\n{file.Path} differs:\nclean:       {expected}\nincremental: {actual}");
        }

        return incremental.UnitsReplayed;
    }

    private async Task WriteSiteAsync(Random random)
    {
        await Source.WriteTextFileAsync("/_data/site.yml", "title: Douze\nauthor: me");
        await Source.WriteTextFileAsync("/_data/menu.yml", "- Home\n- About");
        await Source.WriteTextFileAsync("/_data/layout.json", "\"layout.html\"");
        await Source.WriteTextFileAsync("/_data/products.json", Products(0));
        await Source.WriteTextFileAsync("/_content/blog/blog.yml", "layout: post.html\nsection: Blog");
        await Source.WriteTextFileAsync("/_includes/layout.html", Layout(0));
        await Source.WriteTextFileAsync("/_includes/post.html", Post(0));
        await Source.WriteTextFileAsync("/_includes/nav.liquid", Nav(0));
        await Source.WriteTextFileAsync("/_includes/tags.liquid", "<ul>{% for tag in tags %}<li>{{ tag }}</li>{% endfor %}</ul>");
        await Source.WriteTextFileAsync("/_content/index.md", "---\ntitle: Home\ndate: 2019-01-01\n---\n# {{ site.title }}\n\n{{ collections.all | size }} pages, {% for p in collections.red %}{{ p.data.title }} {% endfor %}");
        await Source.WriteTextFileAsync("/_content/products.md", "---\ntitle: Products\ndate: 2019-01-02\npagination:\n  data: products\n  size: 5\n  alias: page_products\n---\n{% for p in page_products %}{{ p.name }} {% endfor %} page {{ pagination.pageNumber }}");
        await Source.WriteTextFileAsync("/_content/latest.md", "---\ntitle: Latest\ndate: 2019-01-03\npagination:\n  data: collections.blue\n  size: 3\n---\n{% for p in pagination.items %}{{ p.data.title }} {% endfor %}");

        for (var i = 0; i < Pages; i++)
        {
            await Source.WriteTextFileAsync(PagePath(i), Page(i, random, 0));
        }
    }

    private static string PagePath(int i) => $"/_content/{Sections[i % Sections.Length]}/page-{i:00}.md";

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

        return builder.ToString();
    }

    private static string Products(int version) => "[" + string.Join(", ", Enumerable.Range(0, 12).Select(i => $"{{ \"name\": \"P{i}v{(i == version % 12 ? version : 0)}\" }}")) + "]";

    private static string Layout(int version) => $"<html><title>{{{{ title }}}} - {{{{ site.title }}}}</title>{{% include 'nav' %}}<main>{{{{ content }}}}</main><footer>v{version} {{{{ section }}}}</footer></html>";

    private static string Post(int version) => $"---\nlayout: layout.html\nsection: Posts\n---\n<article>{{{{ content }}}}{{% include 'tags' %}} post v{version}</article>";

    private static string Nav(int version) => $"<nav v{version}>{{% for p in collections.all limit: 3 %}}<a href=\"{{{{ p.data.url }}}}\">{{{{ p.data.title }}}}</a>{{% endfor %}}</nav>";

    /// <summary>
    /// Applies one random edit and describes it.
    /// </summary>
    private async Task<string> EditAsync(Random random)
    {
        var version = random.Next(1, 1000);

        switch (random.Next(9))
        {
            case 0:
            case 1:
                {
                    // Body and front matter (title, tags, menu use) of a page.
                    var i = random.Next(Pages);
                    await Source.WriteTextFileAsync(PagePath(i), Page(i, random, version));
                    return $"page {i} v{version}";
                }

            case 2:
                {
                    // The body only, with the same date.
                    var i = random.Next(Pages);
                    var path = PagePath(i);
                    var date = (await Source.GetFileMetadataAsync(path)).LastWriteTimeUtc;
                    var text = await Source.ReadAllTextAsync(path);
                    await Source.WriteTextFileAsync(path, text + $"\n\nMore {version}.");
                    Source.SetLastWriteTimeUtc(path, date);
                    return $"body of page {i} v{version}";
                }

            case 3:
                await Source.WriteTextFileAsync("/_includes/layout.html", Layout(version));
                return $"layout v{version}";
            case 4:
                await Source.WriteTextFileAsync("/_includes/post.html", Post(version));
                return $"post layout v{version}";
            case 5:
                await Source.WriteTextFileAsync("/_includes/nav.liquid", Nav(version));
                return $"nav v{version}";
            case 6:
                await Source.WriteTextFileAsync("/_data/products.json", Products(version));
                return $"products v{version}";
            case 7:
                await Source.WriteTextFileAsync(random.Next(2) == 0 ? "/_data/site.yml" : "/_data/menu.yml", random.Next(2) == 0 ? $"title: Douze {version}\nauthor: me" : $"- Home\n- About {version}");
                return $"site data v{version}";
            default:
                await Source.WriteTextFileAsync("/_content/blog/blog.yml", random.Next(2) == 0 ? $"layout: post.html\nsection: Blog {version}" : $"section: Blog {version}");
                return $"folder data v{version}";
        }
    }
}
