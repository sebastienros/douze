using Douze.FileSystems;
using Douze.Progress;
using Douze.Workflow;
using Douze.Workflow.Watch;
using Xunit;
using static Douze.Tests.Workflow.Cache.CacheTestBase;
using static Douze.Tests.Workflow.Watch.TestSession;

namespace Douze.Tests.Workflow.Watch;

/// <summary>
/// The builds of a watch session: what each change runs again, writes and deletes.
/// </summary>
public class IncrementalBuilderTests
{
    private readonly MemoryFileSystem _source = new();

    // The nav lists the first two pages by date (index, about); the home page lists the posts; the about page the menu.
    // Posts use post.html, which includes share.html, then layout.html.
    private async Task WriteSiteAsync()
    {
        await _source.WriteTextFileAsync("/_data/site.yml", "title: Douze");
        await _source.WriteTextFileAsync("/_data/menu.yml", "- Home\n- About");
        await _source.WriteTextFileAsync("/_data/layout.json", "\"layout.html\"");
        await _source.WriteTextFileAsync("/_includes/layout.html", "<html><body>{% include 'nav' %}<main>{{ content }}</main><footer>{{ site.title }}</footer></body></html>");
        await _source.WriteTextFileAsync("/_includes/nav.liquid", "<nav>{% for p in collections.all limit: 2 %}<a href=\"{{ p.data.url }}\">{{ p.data.title }}</a>{% endfor %}</nav>");
        await _source.WriteTextFileAsync("/_includes/post.html", "---\nlayout: layout.html\n---\n<article>{{ content }}{% include 'share' %}</article>");
        await _source.WriteTextFileAsync("/_includes/share.html", "<p>share</p>");
        await _source.WriteTextFileAsync("/_content/index.md", "---\ntitle: Home\ndate: 2020-01-01\n---\n# {{ site.title }}\n\n{% for post in collections.post %}{{ post.data.title }} {% endfor %}");
        await _source.WriteTextFileAsync("/_content/about.md", "---\ntitle: About\ndate: 2020-01-02\n---\n{% for m in menu %}{{ m }} {% endfor %}");
        await _source.WriteTextFileAsync("/_content/styles.css", "body { color: red; }");

        foreach (var (name, day) in new[] { ("a", 1), ("b", 2), ("c", 3) })
        {
            await _source.WriteTextFileAsync($"/_content/posts/{name}.md", Post(name, day));
        }
    }

    private static string Post(string name, int day, string body = "body") => $"---\ntitle: {name.ToUpperInvariant()}\ndate: 2020-02-0{day}\ntags: [post]\nlayout: post.html\n---\n{name} {body}";

    private static string[] Changed(RebuildResult result) => [.. result.Changed.Select(x => x.FullName)];

    private static string[] Executed(RebuildResult result, string step) => [.. result.Build[step].Results.Where(x => !x.Replayed).Select(x => x.Key.Unit).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task NoChangeWritesNothing()
    {
        await WriteSiteAsync();
        using var session = new TestSession(_source);

        var first = await session.BuildSuccessfullyAsync();
        Assert.Contains("/posts/a/index.html", Changed(first));
        Assert.Contains("/styles.css", Changed(first));

        var second = await session.BuildSuccessfullyAsync(new ChangeSet());

        Assert.Equal(0, second.Build.UnitsExecuted);
        Assert.Empty(second.Changed);
    }

    [Fact]
    public async Task ChangesAreWhatTheSourceFolderSays()
    {
        await WriteSiteAsync();
        using var session = new TestSession(_source);
        await session.BuildSuccessfullyAsync();

        // FSEvents reports an edit of a recently created file as a creation.
        await _source.WriteTextFileAsync("/_content/about.md", "---\ntitle: About\n---\nAbout");
        await _source.WriteTextFileAsync("/_content/new.md", "New");
        await _source.DeleteFileAsync("/_content/posts/a.md");
        var changes = Changes(("/_content/about.md", ChangeKind.Added), ("/_content/new.md", ChangeKind.Modified), ("/_content/posts/a.md", ChangeKind.Modified), ("/_content/nothing.md", ChangeKind.Deleted));
        await session.BuildSuccessfullyAsync(changes);

        Assert.Equal(
            [("/_content/about.md", ChangeKind.Modified), ("/_content/new.md", ChangeKind.Added), ("/_content/nothing.md", ChangeKind.Deleted), ("/_content/posts/a.md", ChangeKind.Deleted)],
            changes.Changes.Select(x => (x.Path.FullName, x.Kind)));
    }

    [Fact]
    public async Task DeletedPageRemovesItsOutputs()
    {
        await WriteSiteAsync();
        using var session = new TestSession(_source);
        await session.BuildSuccessfullyAsync();

        await _source.DeleteFileAsync("/_content/posts/b.md");
        var result = await session.BuildSuccessfullyAsync(Changes(("/_content/posts/b.md", ChangeKind.Deleted)));

        Assert.False((await session.Output.GetFileEntryAsync("/posts/b/index.html")).Exists);
        Assert.False((await session.Output.GetDirectoryEntryAsync("/posts/b/")).Exists);
        Assert.Equal(["/posts/b/index.html"], result.Build.Deleted.Select(x => x.FullName));

        // The home page lists the posts.
        Assert.Equal(["/index.html", "/posts/b/index.html"], Changed(result));
        Assert.Contains("A C", await session.Output.ReadAllTextAsync("/index.html"));
    }

    [Fact]
    public async Task RenamedPageMovesItsOutputs()
    {
        await WriteSiteAsync();
        using var session = new TestSession(_source);
        await session.BuildSuccessfullyAsync();

        var text = await _source.ReadAllTextAsync("/_content/posts/c.md");
        await _source.DeleteFileAsync("/_content/posts/c.md");
        await _source.WriteTextFileAsync("/_content/articles/c.md", text);

        var changes = new ChangeSet();
        changes.Rename("/_content/posts/c.md", "/_content/articles/c.md");
        var result = await session.BuildSuccessfullyAsync(changes);

        Assert.False((await session.Output.GetFileEntryAsync("/posts/c/index.html")).Exists);
        Assert.Contains("c body", await session.Output.ReadAllTextAsync("/articles/c/index.html"));
        Assert.Equal(["/articles/c/index.html", "/posts/c/index.html"], Changed(result));
    }

    [Fact]
    public async Task IncludeEditRendersItsUsersOnly()
    {
        await WriteSiteAsync();
        using var session = new TestSession(_source);
        await session.BuildSuccessfullyAsync();

        await _source.WriteTextFileAsync("/_includes/share.html", "<p>share it</p>");
        var result = await session.BuildSuccessfullyAsync(Changes(("/_includes/share.html", ChangeKind.Modified)));

        Assert.Equal(["/posts/a/index.html", "/posts/b/index.html", "/posts/c/index.html"], Executed(result, "layout"));
        Assert.Equal(["/posts/a/index.html", "/posts/b/index.html", "/posts/c/index.html"], Changed(result));
        Assert.Contains("<p>share it</p>", await session.Output.ReadAllTextAsync("/posts/a/index.html"));
    }

    [Fact]
    public async Task DataEditRendersItsReadersOnly()
    {
        await WriteSiteAsync();
        using var session = new TestSession(_source);
        await session.BuildSuccessfullyAsync();

        await _source.WriteTextFileAsync("/_data/menu.yml", "- Home\n- About\n- Blog");
        var result = await session.BuildSuccessfullyAsync(Changes(("/_data/menu.yml", ChangeKind.Modified)));

        Assert.Equal(["/about/index.html"], Changed(result));
        Assert.Contains("Home About Blog", await session.Output.ReadAllTextAsync("/about/index.html"));
    }

    [Fact]
    public async Task AddedPageJoinsTheCollections()
    {
        await WriteSiteAsync();
        using var session = new TestSession(_source);
        await session.BuildSuccessfullyAsync();

        await _source.WriteTextFileAsync("/_content/posts/d.md", Post("d", 4));
        var result = await session.BuildSuccessfullyAsync(Changes(("/_content/posts/d.md", ChangeKind.Added)));

        Assert.Equal(["/index.html", "/posts/d/index.html"], Changed(result));
        Assert.Contains("A B C D", await session.Output.ReadAllTextAsync("/index.html"));

        // A folder added at once (a copy, a checkout) is reported alone.
        await _source.WriteTextFileAsync("/_content/notes/e.md", Post("e", 5));
        await _source.WriteTextFileAsync("/_content/notes/f.md", Post("f", 6));
        result = await session.BuildSuccessfullyAsync(Changes(("/_content/notes", ChangeKind.Added)));

        Assert.Equal(["/index.html", "/notes/e/index.html", "/notes/f/index.html"], Changed(result));
    }

    [Fact]
    public async Task TemplatesAreReadAgainOnceChanged()
    {
        await WriteSiteAsync();
        using var session = new TestSession(_source);
        await session.BuildSuccessfullyAsync();

        for (var i = 0; i < 3; i++)
        {
            await _source.WriteTextFileAsync("/_includes/share.html", $"<p>share {i}</p>");
            await _source.WriteTextFileAsync("/_includes/layout.html", $"<html><body>v{i} {{{{ content }}}}</body></html>");
            await session.BuildSuccessfullyAsync(Changes(("/_includes/share.html", ChangeKind.Modified), ("/_includes/layout.html", ChangeKind.Modified)));

            var post = await session.Output.ReadAllTextAsync("/posts/b/index.html");
            Assert.Contains($"<p>share {i}</p>", post);
            Assert.Contains($"v{i} ", post);
        }

        // The include of every edit was parsed; only the current one is kept, with the nav the first layout included.
        Assert.Equal(2, session.Builder.Templates!.IncludeCount);
    }

    [Fact]
    public async Task AFailedBuildDeletesNothing()
    {
        await WriteSiteAsync();
        using var session = new TestSession(_source);
        await session.BuildSuccessfullyAsync();

        // b is deleted while c does not render: the output of b stays until a build succeeds.
        await _source.DeleteFileAsync("/_content/posts/b.md");
        await _source.WriteTextFileAsync("/_content/posts/c.md", Post("c", 3, "{% if %}"));
        var failed = await session.BuildAsync(Changes(("/_content/posts/b.md", ChangeKind.Deleted), ("/_content/posts/c.md", ChangeKind.Modified)));

        Assert.False(failed.Build.Succeeded);
        Assert.True((await session.Output.GetFileEntryAsync("/posts/b/index.html")).Exists);

        await _source.WriteTextFileAsync("/_content/posts/c.md", Post("c", 3, "fixed"));
        var fixedBuild = await session.BuildSuccessfullyAsync(Changes(("/_content/posts/c.md", ChangeKind.Modified)));

        Assert.False((await session.Output.GetFileEntryAsync("/posts/b/index.html")).Exists);
        Assert.Contains("/posts/b/index.html", Changed(fixedBuild));
    }

    [Fact]
    public async Task WithoutTheCacheStaleOutputsAreDeletedToo()
    {
        await WriteSiteAsync();
        using var session = new TestSession(_source, useCache: false);
        await session.BuildSuccessfullyAsync();

        await _source.DeleteFileAsync("/_content/posts/a.md");
        var result = await session.BuildSuccessfullyAsync(Changes(("/_content/posts/a.md", ChangeKind.Deleted)));

        Assert.False((await session.Output.GetFileEntryAsync("/posts/a/index.html")).Exists);
        Assert.Equal(["/posts/a/index.html"], result.Build.Deleted.Select(x => x.FullName));
    }

    [Fact]
    public async Task AChangedWorkflowTakesEffectAndAnInvalidOneWaits()
    {
        await WriteSiteAsync();

        // The workflow drops the posts while 'drop' is set; it is invalid while 'invalid' is.
        var drop = false;
        var invalid = false;

        WorkflowDefinition Workflow(SiteOptions site)
        {
            if (invalid)
            {
                throw new WorkflowValidationException("douze.yml(1,1): error: something is wrong");
            }

            var workflow = DefaultWorkflow.Create(site);

            return drop
                ? workflow with { Steps = [.. workflow.Steps.Select(x => x.Name == "pages" ? x with { Inputs = [.. x.Inputs, "!_content/posts/**"] } : x)] }
                : workflow;
        }

        using var session = new TestSession(_source, workflow: Workflow);
        var first = await session.BuildSuccessfullyAsync();
        var graph = session.Builder.Graph;

        // Unchanged: the same graph (its steps, its template loader).
        await session.BuildSuccessfullyAsync(new ChangeSet());
        Assert.Same(graph, session.Builder.Graph);

        invalid = true;
        var messages = new MessageView(session.Messages);
        Assert.Null(await session.Builder.BuildAsync(Changes(("/douze.yml", ChangeKind.Modified)), messages, TestContext.Current.CancellationToken));
        Assert.Contains(session.Messages, x => x.Level == LogLevel.Error && x.Message.Contains("something is wrong", StringComparison.Ordinal));

        invalid = false;
        drop = true;
        var result = await session.BuildSuccessfullyAsync(Changes(("/douze.yml", ChangeKind.Modified)));

        Assert.NotSame(graph, session.Builder.Graph);
        Assert.False((await session.Output.GetFileEntryAsync("/posts/a/index.html")).Exists);
        Assert.Contains("/posts/a/index.html", result.Build.Deleted.Select(x => x.FullName));
    }
}
