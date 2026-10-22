using Douze.FileSystems;
using Douze.Nodes;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow.Cache;

/// <summary>
/// Which units a build runs again after an edit, through the default workflow and the disk cache.
/// </summary>
public class EarlyCutoffTests : CacheTestBase
{
    // Collections by date: index, about, a, b, c. The nav lists the first two pages, the home page lists the posts, the
    // about page lists the menu; posts use post.html, which includes share.html, then layout.html.
    private async Task WriteSiteAsync()
    {
        await Source.WriteTextFileAsync("/_data/site.yml", "title: Douze");
        await Source.WriteTextFileAsync("/_data/menu.yml", "- Home\n- About");
        await Source.WriteTextFileAsync("/_data/layout.json", "\"layout.html\"");
        await Source.WriteTextFileAsync("/_includes/layout.html", "<html>{% include 'nav' %}<main>{{ content }}</main><footer>{{ site.title }}</footer></html>");
        await Source.WriteTextFileAsync("/_includes/nav.liquid", "<nav>{% for p in collections.all limit: 2 %}<a href=\"{{ p.data.url }}\">{{ p.data.title }}</a>{% endfor %}</nav>");
        await Source.WriteTextFileAsync("/_includes/post.html", "---\nlayout: layout.html\n---\n<article>{{ content }}{% include 'share' %}</article>");
        await Source.WriteTextFileAsync("/_includes/share.html", "<p>share</p>");
        await Source.WriteTextFileAsync("/_content/index.md", "---\ntitle: Home\ndate: 2020-01-01\n---\n# {{ site.title }}\n\n{% for post in collections.post %}{{ post.data.title }} {% endfor %}");
        await Source.WriteTextFileAsync("/_content/about.md", "---\ntitle: About\ndate: 2020-01-02\n---\n{% for m in menu %}{{ m }} {% endfor %}");

        foreach (var (name, day) in new[] { ("a", 1), ("b", 2), ("c", 3) })
        {
            await Source.WriteTextFileAsync($"/_content/posts/{name}.md", $"---\ntitle: {name.ToUpperInvariant()}\ndate: 2020-02-0{day}\ntags: [post]\nlayout: post.html\n---\n{name} body");
        }
    }

    /// <summary>
    /// Rewrites a file. With <paramref name="keepDate"/>, its modification time (and so <c>page.date</c>) stays the same.
    /// </summary>
    private async Task EditAsync(FPath path, string content, bool keepDate = false)
    {
        var date = (await Source.GetFileMetadataAsync(path)).LastWriteTimeUtc;
        await Source.WriteTextFileAsync(path, content);

        if (keepDate)
        {
            Source.SetLastWriteTimeUtc(path, date);
        }
    }

    private async Task<string> OutputAsync(FPath path) => await Output.ReadAllTextAsync(path);

    [Fact]
    public async Task NoChangeBuildExecutesZeroUnits()
    {
        await WriteSiteAsync();

        var first = await BuildCachedAsync();
        Assert.Equal(first.Steps.Sum(x => x.Units), first.UnitsExecuted);

        var second = await BuildCachedAsync();

        Assert.Equal(0, second.UnitsExecuted);
        Assert.Equal(first.UnitsExecuted, second.UnitsReplayed);
        Assert.Equal(first.Outputs, second.Outputs);
        Assert.Equal(first.Scopes.Hash, second.Scopes.Hash);
    }

    [Fact]
    public async Task BodyEditRerunsThatPageOnly()
    {
        await WriteSiteAsync();
        await BuildCachedAsync();

        // The metadata does not change: the collections are not built again, nothing that reads them runs.
        await EditAsync("/_content/posts/c.md", "---\ntitle: C\ndate: 2020-02-03\ntags: [post]\nlayout: post.html\n---\nnew c body", keepDate: true);
        var result = await BuildCachedAsync();

        Assert.Equal(["pages", "permalink", "paginate", "render", "markdown", "layout", "write"], StepsThatRan(result));
        Assert.Equal(["/posts/c/index.html"], Executed(result, "layout"));
        Assert.Equal(["/posts/c/index.html"], Executed(result, "write"));
        Assert.Contains("new c body", await OutputAsync("/posts/c/index.html"));

        // A real edit changes the file's date, so page.date: the collections are built again, and the home page, which
        // lists the posts, renders again; the nav lists the first two pages only, so the other layouts do not run.
        await EditAsync("/_content/posts/c.md", "---\ntitle: C\ndate: 2020-02-03\ntags: [post]\nlayout: post.html\n---\nnewer c body");
        result = await BuildCachedAsync();

        Assert.Equal(["*"], Executed(result, "collections"));
        Assert.Equal(["/index.md", "/posts/c/index.md"], Executed(result, "render"));
        Assert.Equal(["/posts/c/index.md"], Executed(result, "markdown"));
        Assert.Equal(["/posts/c/index.html"], Executed(result, "layout"));
    }

    [Fact]
    public async Task TagEditRerunsCollectAndTheRendersThatReadIt()
    {
        await WriteSiteAsync();
        await BuildCachedAsync();

        await EditAsync("/_content/posts/b.md", "---\ntitle: B\ndate: 2020-02-02\ntags: [post, featured]\nlayout: post.html\n---\nb body", keepDate: true);
        var result = await BuildCachedAsync();

        // The home page read collections.post, which holds b; the nav reads the first two pages, which did not change.
        Assert.Equal(["*"], Executed(result, "collections"));
        Assert.Equal(["/index.md", "/posts/b/index.md"], Executed(result, "render"));
        Assert.Equal(["/posts/b/index.html"], Executed(result, "layout"));
        Assert.Equal(["featured", "post"], result.Scopes.Site.Single(x => x.Source == "step:collections").Values["collections"]!.AsObject().Select(x => x.Key).Where(x => x is not ("all" or "urls")).Order(StringComparer.Ordinal));

        // A title among the first two pages changes the nav, so every layout.
        await EditAsync("/_content/about.md", "---\ntitle: About us\ndate: 2020-01-02\n---\n{% for m in menu %}{{ m }} {% endfor %}", keepDate: true);
        result = await BuildCachedAsync();

        Assert.Equal(["/about/index.html", "/index.html", "/posts/a/index.html", "/posts/b/index.html", "/posts/c/index.html"], Executed(result, "layout"));
        Assert.Contains(">About us</a>", await OutputAsync("/posts/a/index.html"));
    }

    [Fact]
    public async Task IncludeEditRerunsOnlyItsUsers()
    {
        await WriteSiteAsync();
        await BuildCachedAsync();

        await EditAsync("/_includes/share.html", "<p>share this</p>");
        var result = await BuildCachedAsync();

        Assert.Equal(["layout", "write"], StepsThatRan(result));
        Assert.Equal(["/posts/a/index.html", "/posts/b/index.html", "/posts/c/index.html"], Executed(result, "layout"));
        Assert.Contains("share this", await OutputAsync("/posts/b/index.html"));

        // 'share' is looked for as share.liquid first: a file added there is used instead.
        await Source.WriteTextFileAsync("/_includes/share.liquid", "<p>liquid share</p>");
        result = await BuildCachedAsync();

        Assert.Equal(["/posts/a/index.html", "/posts/b/index.html", "/posts/c/index.html"], Executed(result, "layout"));
        Assert.Contains("liquid share", await OutputAsync("/posts/b/index.html"));
    }

    [Fact]
    public async Task ShortcodeEditRerunsOnlyItsUsers()
    {
        await WriteSiteAsync();
        await Source.WriteTextFileAsync("/_includes/shortcodes/note.liquid", "---\nblock: true\n---\n<aside>{{ body }}</aside>");
        await Source.WriteTextFileAsync("/_includes/shortcodes/badge.liquid", "<b>{{ args[0] }}</b>");
        await EditAsync("/_content/posts/a.md", "---\ntitle: A\ndate: 2020-02-01\ntags: [post]\nlayout: post.html\n---\n{% note %}a *body*{% endnote %}", keepDate: true);
        await EditAsync("/_content/posts/b.md", "---\ntitle: B\ndate: 2020-02-02\ntags: [post]\nlayout: post.html\n---\nb {% badge \"new\" %}", keepDate: true);
        await BuildCachedAsync();

        await EditAsync("/_includes/shortcodes/note.liquid", "---\nblock: true\n---\n<aside class=\"note\">{{ body }}</aside>");
        var result = await BuildCachedAsync();

        Assert.Equal(["/posts/a/index.md"], Executed(result, "render"));
        Assert.Equal(["/posts/a/index.html"], Executed(result, "layout"));
        Assert.Contains("<aside class=\"note\"><p>a <em>body</em></p>\n</aside>", await OutputAsync("/posts/a/index.html"));
    }

    [Fact]
    public async Task MarkdownIncludeEditRerunsOnlyItsUsers()
    {
        await WriteSiteAsync();
        await Source.WriteTextFileAsync("/_content/partials/options.md", "---\npermalink: false\n---\n- `--all`");
        await EditAsync("/_content/posts/c.md", "---\ntitle: C\ndate: 2020-02-03\ntags: [post]\nlayout: post.html\n---\n{% include_markdown \"partials/options.md\" %}", keepDate: true);
        await BuildCachedAsync();

        await EditAsync("/_content/partials/options.md", "---\npermalink: false\n---\n- `--none`", keepDate: true);
        var result = await BuildCachedAsync();

        // The partial is not a page: its permalink is false.
        Assert.Equal(["/posts/c/index.md"], Executed(result, "render"));
        Assert.Equal(["/posts/c/index.html"], Executed(result, "layout"));
        Assert.Contains("<code>--none</code>", await OutputAsync("/posts/c/index.html"));
    }

    [Fact]
    public async Task DataFileEditRerunsOnlyTheUnitsThatResolvedItsKeys()
    {
        await WriteSiteAsync();
        await BuildCachedAsync();

        await EditAsync("/_data/menu.yml", "- Home\n- About\n- Blog");
        var result = await BuildCachedAsync();

        // Every page resolves site.title, layout and the collections: only the about page read the menu.
        Assert.Equal(["*"], Executed(result, "site-data"));
        Assert.Equal(["/about/index.md"], Executed(result, "render"));
        Assert.Equal(["/about/index.html"], Executed(result, "layout"));
        Assert.Contains("Home About Blog", await OutputAsync("/about/index.html"));

        // A folder data file added on the path of a page shadows the site data it resolved.
        await Source.WriteTextFileAsync("/_content/posts/posts.yml", "site:\n  title: Posts");
        result = await BuildCachedAsync();

        Assert.Equal(["/posts/a/index.html", "/posts/b/index.html", "/posts/c/index.html"], Executed(result, "layout"));
        Assert.Contains("<footer>Posts</footer>", await OutputAsync("/posts/a/index.html"));
        Assert.Contains("<footer>Douze</footer>", await OutputAsync("/index.html"));
    }

    [Fact]
    public async Task DateChangeWithoutAnEditRerunsThePage()
    {
        await WriteSiteAsync();
        await Source.WriteTextFileAsync("/_content/dated.md", "---\ntitle: Dated\n---\n{{ page.date | date: '%Y' }}");
        Source.SetLastWriteTimeUtc("/_content/dated.md", new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        await BuildCachedAsync();
        Assert.Contains("2020", await OutputAsync("/dated/index.html"));

        // A touched file (a checkout, a copy) has the same content and a new page.date.
        Source.SetLastWriteTimeUtc("/_content/dated.md", new DateTime(2021, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var result = await BuildCachedAsync();

        Assert.Equal(["/_content/dated.md"], Executed(result, "pages"));
        Assert.Contains("2021", await OutputAsync("/dated/index.html"));
    }

    [Fact]
    public async Task NonIncrementalStepAlwaysRuns()
    {
        await Source.WriteTextFileAsync("/a.txt", "a");
        await Source.WriteTextFileAsync("/b.txt", "b");

        var workflow = new WorkflowDefinition(
        [
            new StepDefinition { Name = "pages", Inputs = ["**/*.txt"], Uses = "frontmatter", Match = ["**/*.txt"], Incremental = false },
            new StepDefinition { Name = "write", Needs = ["pages"], Uses = "write" },
        ], null);

        await BuildCachedAsync(workflow);
        var result = await BuildCachedAsync(workflow);

        // Its outputs did not change: the steps after it replay.
        Assert.Equal(["/a.txt", "/b.txt"], Executed(result, "pages"));
        Assert.Empty(Executed(result, "write"));
    }

    [Fact]
    public async Task DeletedOutputIsWrittenAgain()
    {
        await WriteSiteAsync();
        await BuildCachedAsync();

        Output = new MemoryFileSystem();
        Site = null;
        var result = await BuildCachedAsync();

        Assert.Equal(["write"], StepsThatRan(result));
        Assert.Contains("a body", await OutputAsync("/posts/a/index.html"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddingATranslationRerunsOnlyThePagesOfItsUrl(bool fallback)
    {
        await Source.WriteTextFileAsync("/_content/docs/index.md", "home");
        await Source.WriteTextFileAsync("/_content/docs/guide.md", "guide");
        await Source.WriteTextFileAsync("/_content/docs/start.md", "start");
        await Source.WriteTextFileAsync("/_content/docs/ja/start.md", "start ja");
        await Source.WriteTextFileAsync("/_content/about.md", "about");

        Site = await SiteOptions.DetectAsync(Source, Output, null, null, Douze.Workflow.Yaml.WorkflowLoader.Load(
            $"site:\n  locales: {{ default: en, root: docs, fallback: {(fallback ? "true" : "false")}, languages: {{ ja: {{ lang: ja }} }} }}\n", "douze.yml").Site);

        var workflow = new WorkflowDefinition([
            new StepDefinition { Name = "pages", Inputs = ["_content/**"], Uses = "frontmatter" },
            new StepDefinition { Name = "permalink", Needs = ["pages"], Uses = "permalink" },
            new StepDefinition { Name = "collections", Needs = ["permalink"], Uses = "collect" },
            new StepDefinition { Name = "localize", Needs = ["permalink", "collections"], Uses = "localize" },
            new StepDefinition { Name = "render", Needs = ["localize"], Uses = "liquid" },
            new StepDefinition { Name = "write", Needs = ["render"], Uses = "write" },
        ], null);

        await Source.WriteTextFileAsync("/_content/docs/guide.md", "{% for a in alternates %}{{ a.url }} {% endfor %}");
        var first = await BuildCachedAsync(workflow);

        // With fallback, /ja/guide/ is served by a copy of /guide/: both list both urls.
        var alternates = fallback ? "/guide/ /ja/guide/ " : "/guide/ ";
        Assert.Equal(alternates, await OutputAsync("/guide/index.md"));
        Assert.Equal(fallback, (await Output.GetFileEntryAsync("/ja/guide/index.md")).Exists);

        await Source.WriteTextFileAsync("/_content/docs/ja/guide.md", "ガイド");
        var result = await BuildCachedAsync(workflow);

        // The new page, and the page whose url it translates; not the other pages, which looked for other urls. With
        // fallback, the alternates of /guide/ are the same: it does not render again.
        Assert.Equal(["/guide/index.md", "/ja/guide/index.md"], Executed(result, "localize"));
        Assert.Equal(fallback ? ["/ja/guide/index.md"] : ["/guide/index.md", "/ja/guide/index.md"], Executed(result, "render"));
        Assert.Equal("/guide/ /ja/guide/ ", await OutputAsync("/guide/index.md"));
        Assert.Equal("ガイド", await OutputAsync("/ja/guide/index.md"));
        Assert.True(first.UnitsExecuted > result.UnitsExecuted);
    }
}
