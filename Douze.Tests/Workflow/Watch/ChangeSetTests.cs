using Douze.FileSystems;
using Douze.Workflow.Watch;
using Xunit;

namespace Douze.Tests.Workflow.Watch;

public class ChangeSetTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "douze-watch-root");

    private static string Disk(string path) => Path.Combine(Root, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void EventsOfAPathMerge()
    {
        var changes = new ChangeSet();

        changes.Add("/a.md", ChangeKind.Added);
        changes.Add("/a.md", ChangeKind.Modified);
        changes.Add("/b.md", ChangeKind.Deleted);
        changes.Add("/b.md", ChangeKind.Added);
        changes.Add("/c.md", ChangeKind.Added);
        changes.Add("/c.md", ChangeKind.Deleted);
        changes.Add("/d.md", ChangeKind.Modified);
        changes.Add("/d.md", ChangeKind.Deleted);

        Assert.Equal(
            [("/a.md", ChangeKind.Added), ("/b.md", ChangeKind.Modified), ("/c.md", ChangeKind.Deleted), ("/d.md", ChangeKind.Deleted)],
            changes.Changes.Select(x => (x.Path.FullName, x.Kind)));
        Assert.Equal("4 files changed", changes.ToString());
    }

    [Fact]
    public void RenameIsDeleteAndAdd()
    {
        var changes = new ChangeSet();
        changes.Rename("/_content/a.md", "/_content/b.md");

        Assert.Equal([("/_content/a.md", ChangeKind.Deleted), ("/_content/b.md", ChangeKind.Added)], changes.Changes.Select(x => (x.Path.FullName, x.Kind)));

        // The file of a changed folder changed too: a folder renamed or deleted is reported without its files.
        changes = new ChangeSet();
        changes.Rename("/_content/posts", "/_content/articles");
        Assert.True(changes.Contains("/_content/posts/a.md"));
        Assert.True(changes.Contains("/_content/articles/b/c.md"));
        Assert.False(changes.Contains("/_content/postscript.md"));
    }

    [Fact]
    public async Task EventsAreDebouncedIntoOneBatch()
    {
        using var watcher = new FileWatcher(Root, _ => false, TimeSpan.FromMilliseconds(100), start: false);
        var ct = TestContext.Current.CancellationToken;

        watcher.Report(ChangeKind.Added, Disk("/_content/a.md~"));
        watcher.ReportRename(Disk("/_content/a.md~"), Disk("/_content/a.md"));
        watcher.Report(ChangeKind.Modified, Disk("/_content/a.md"));
        watcher.Report(ChangeKind.Modified, Disk("/_includes/nav.liquid"));

        var batch = await watcher.WaitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.Equal(
            [("/_content/a.md", ChangeKind.Added), ("/_content/a.md~", ChangeKind.Deleted), ("/_includes/nav.liquid", ChangeKind.Modified)],
            batch.Changes.Select(x => (x.Path.FullName, x.Kind)));

        // A later event is another batch.
        watcher.Report(ChangeKind.Deleted, Disk("/_content/a.md"));
        batch = await watcher.WaitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal("_content/a.md deleted", batch.ToString());

        // Lost events: anything may have changed.
        watcher.ReportLost();
        batch = await watcher.WaitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.True(batch.Rescan);
        Assert.True(batch.Contains("/anything.md"));
    }

    [Fact]
    public async Task IgnoredPathsAreDropped()
    {
        using var watcher = new FileWatcher(Root, path => path.FullName.StartsWith("/_site/", StringComparison.Ordinal), TimeSpan.FromMilliseconds(50), start: false);
        var ct = TestContext.Current.CancellationToken;

        watcher.Report(ChangeKind.Modified, Disk("/_site/index.html"));
        watcher.Report(ChangeKind.Modified, Root);
        watcher.Report(ChangeKind.Modified, Disk("/index.md"));

        // A file outside the source folder (a workflow file) is reported by its full path.
        var outside = Path.Combine(Path.GetTempPath(), "douze.yml");
        watcher.WatchFile(outside);
        watcher.Report(ChangeKind.Modified, outside);

        // Any other path outside the source folder is dropped.
        watcher.Report(ChangeKind.Modified, Path.Combine(Path.GetTempPath(), "other.yml"));

        var batch = await watcher.WaitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.Equal(
            new[] { "/index.md", outside.Replace(Path.DirectorySeparatorChar, '/') }.Order(StringComparer.Ordinal),
            batch.Changes.Select(x => x.Path.FullName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task PathsWithDotsNeverThrow()
    {
        using var watcher = new FileWatcher(Root, _ => false, TimeSpan.FromMilliseconds(50), start: false);
        var ct = TestContext.Current.CancellationToken;

        // Names with two dots: rsync's temporary file of a dot file, a range.
        watcher.Report(ChangeKind.Added, Disk("/_data/..gitignore.Xb3k9"));
        watcher.ReportRename(Disk("/_data/..gitignore.Xb3k9"), Disk("/_data/.gitignore"));
        watcher.Report(ChangeKind.Modified, Disk("/_content/v1..v2.md"));

        // ".." segments are resolved; one that leaves the source folder is outside it.
        watcher.Report(ChangeKind.Modified, Disk("/_content/posts/../index.md"));
        watcher.Report(ChangeKind.Modified, Disk("/../elsewhere/index.md"));

        var batch = await watcher.WaitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.False(batch.Rescan);
        Assert.Equal(
            [("/_content/index.md", ChangeKind.Modified), ("/_content/v1..v2.md", ChangeKind.Modified), ("/_data/..gitignore.Xb3k9", ChangeKind.Deleted), ("/_data/.gitignore", ChangeKind.Added)],
            batch.Changes.Select(x => (x.Path.FullName, x.Kind)).Order());

        // A path that can't be mapped asks for a rescan.
        watcher.Report(ChangeKind.Modified, "");
        batch = await watcher.WaitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.True(batch.Rescan);
    }

    [Fact]
    public void PathsRejectParentSegmentsOnly()
    {
        Assert.Equal("/a/..b/c..d/e..", new FPath("/a/..b/c..d/e..").FullName);
        Assert.Equal("/a/b...", new FPath("/a\\b...").FullName);
        Assert.Throws<ArgumentException>(() => new FPath("/a/../b"));
        Assert.Throws<ArgumentException>(() => new FPath("../b"));
        Assert.Throws<ArgumentException>(() => new FPath("/a/.."));
        Assert.Throws<ArgumentException>(() => new FPath("a\\..\\b"));
    }

    [Fact]
    public async Task OutputCacheAndGitFoldersAreIgnored()
    {
        var root = Path.Combine(Path.GetTempPath(), "douze-tests", Guid.NewGuid().ToString("N"));

        using var builder = new IncrementalBuilder(
            async () =>
            {
                var site = await SiteOptions.DetectAsync(new MemoryFileSystem(), new MemoryFileSystem(), root, Path.Combine(root, "_site"));
                return (Douze.Workflow.DefaultWorkflow.Create(site), site);
            },
            new Douze.Workflow.BuildOptions { UseCache = false },
            Douze.Progress.SilentProgressView.Discard);

        // Before the first build, the site is not known yet.
        Assert.False(builder.IsIgnored("/_site/index.html"));
        Assert.True(builder.IsIgnored("/.git/index"));

        await builder.BuildAsync(null, Douze.Progress.SilentProgressView.Discard, TestContext.Current.CancellationToken);

        Assert.True(builder.IsIgnored("/_site/index.html"));
        Assert.True(builder.IsIgnored("/_site"));
        Assert.True(builder.IsIgnored("/.douze/cache/index.bin"));
        Assert.False(builder.IsIgnored("/_sitemap.md"));
        Assert.False(builder.IsIgnored("/index.md"));
    }

    [Fact]
    public async Task TheWatcherSeesTheDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), "douze-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "_content"));
        var ct = TestContext.Current.CancellationToken;

        try
        {
            using var watcher = new FileWatcher(root, _ => false, TimeSpan.FromMilliseconds(100));

            // FSEvents may report the folder's creation late: wait for the file.
            await File.WriteAllTextAsync(Path.Combine(root, "_content", "a.md"), "a", ct);

            var seen = new ChangeSet();

            while (!seen.Changes.Any(x => x.Path.FullName == "/_content/a.md"))
            {
                seen.Merge(await watcher.WaitAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct));
            }

            File.Move(Path.Combine(root, "_content", "a.md"), Path.Combine(root, "_content", "b.md"));

            seen = new ChangeSet();

            while (!seen.Changes.Any(x => x.Path.FullName == "/_content/b.md") || !seen.Changes.Any(x => x.Path.FullName == "/_content/a.md"))
            {
                seen.Merge(await watcher.WaitAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
