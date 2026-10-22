using Douze.FileSystems;
using Douze.Workflow;
using Douze.Workflow.Watch;
using Xunit;

namespace Douze.Tests.Workflow.Watch;

public class SourceSnapshotTests
{
    [Fact]
    public async Task FilesThatVanishAreLeftOut()
    {
        // An editor saving a file writes a temporary file and renames it: the file can be listed, then be gone.
        var source = new VanishingFileSystem();
        await source.Inner.WriteTextFileAsync("/a.md", "a");
        await source.Inner.WriteTextFileAsync("/b.md", "b");
        source.Vanished.Add("/a.md~");

        var snapshot = await SourceSnapshot.ScanAsync(source, SourceScope.Everything, [], 2, TestContext.Current.CancellationToken);
        Assert.Equal(2, snapshot.Count);

        // A file listed as there whose stat fails is gone too.
        source.Failing.Add("/b.md");
        var kinds = await snapshot.ApplyAsync(source, ["/a.md~", "/b.md"]);

        Assert.Equal([("/b.md", ChangeKind.Deleted)], kinds.Select(x => (x.Path.FullName, x.Kind)));
        Assert.Equal(1, snapshot.Count);
    }

    [Fact]
    public void TheScopeIsTheFoldersThePatternsCanMatchFilesIn()
    {
        var scope = SourceScope.Of([
            GlobSet.Parse("content/**", "!content/icons/**"),
            GlobSet.Parse("content/docs/**/*.md"),
            GlobSet.Parse("data/**/*.{yml,json}", "scripts/client/api/*.js", "content/index.html", "robots.txt"),
        ]);

        Assert.Equal(["/content/", "/data/", "/scripts/client/api/"], scope.Folders.Select(x => x.FullName));
        Assert.Equal(["/robots.txt"], scope.Files.Select(x => x.FullName));
        Assert.True(scope.Contains("/content/a/b.md"));
        Assert.False(scope.Contains("/node_modules/x.js"));
        Assert.True(scope.Touches("/scripts"));
        Assert.False(scope.Touches("/templates/page.html"));

        // A pattern that can match anywhere, or a set starting from everything, reads the whole folder.
        Assert.Same(SourceScope.Everything, SourceScope.Of([GlobSet.Parse("content/**"), GlobSet.Parse("**/*.md")]));
        Assert.Same(SourceScope.Everything, SourceScope.Of([GlobSet.Parse("!drafts/**")]));
        Assert.Same(SourceScope.Everything, SourceScope.Of([GlobSet.All]));
    }

    [Fact]
    public async Task OnlyTheScopeIsListedAndExcludedFoldersAreNotWalked()
    {
        var root = Path.Combine(Path.GetTempPath(), "douze-tests", Guid.NewGuid().ToString("N"));

        try
        {
            foreach (var file in new[] { "content/a.md", "content/sub/b.md", "node_modules/x/c.md", ".douze/cache/d.md", "out/e.md", "robots.txt" })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, file))!);
                await File.WriteAllTextAsync(Path.Combine(root, file), file, TestContext.Current.CancellationToken);
            }

            var source = new WalkCountingFileSystem(root);
            var ct = TestContext.Current.CancellationToken;

            var scoped = await SourceSnapshot.ScanAsync(source, SourceScope.Of([GlobSet.Parse("content/**", "robots.txt", "missing.txt")]), ["/.douze/"], 2, ct);
            Assert.Equal(["/content/a.md", "/content/sub/b.md", "/robots.txt"], scoped.Matching(WorkflowGraphFor("content/**", "robots.txt", "missing.txt"), RootsOf(), ["/.douze/"]).Select(x => x.Path.FullName));
            Assert.DoesNotContain(source.Folders, x => x.StartsWith("/node_modules", StringComparison.Ordinal) || x.StartsWith("/.douze", StringComparison.Ordinal));

            // The whole folder but the excluded ones, which are not walked.
            source.Folders.Clear();
            var all = await SourceSnapshot.ScanAsync(source, SourceScope.Everything, ["/.douze/", "/out/"], 2, ct);
            Assert.Equal(4, all.Count);
            Assert.DoesNotContain(source.Folders, x => x.StartsWith("/.douze", StringComparison.Ordinal) || x.StartsWith("/out", StringComparison.Ordinal));

            // A workflow that reads another folder needs another scan; the excluded folders must be the same too.
            Assert.True(scoped.Covers(SourceScope.Of([GlobSet.Parse("content/sub/**")]), ["/.douze/"]));
            Assert.False(scoped.Covers(SourceScope.Of([GlobSet.Parse("content/**", "data/**")]), ["/.douze/"]));
            Assert.False(scoped.Covers(SourceScope.Of([GlobSet.Parse("content/**")]), ["/.douze/", "/out/"]));

            // A change outside the scope does not enter the snapshot.
            await File.WriteAllTextAsync(Path.Combine(root, "node_modules/x/new.md"), "new", ct);
            Assert.Empty(await scoped.ApplyAsync(source, ["/node_modules/x/new.md"]));
            Assert.Equal(3, scoped.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static WorkflowGraph? _graph;

    private static WorkflowGraph WorkflowGraphFor(params string[] inputs)
    {
        return _graph = WorkflowGraph.Build(new WorkflowDefinition([new StepDefinition { Name = "pages", Inputs = [.. inputs], Uses = "frontmatter" }], null));
    }

    private static IReadOnlyList<GraphNode> RootsOf() => [.. _graph!.Nodes.Where(x => x.Inputs != null)];

    /// <summary>
    /// A physical file system that records the folders it lists.
    /// </summary>
    private sealed class WalkCountingFileSystem(string root) : IFileSystem
    {
        private readonly PhysicalFileSystem _inner = new(root);

        public System.Collections.Concurrent.ConcurrentBag<string> Folders { get; } = [];

        public ValueTask<IEnumerable<FileSystemEntry>> EnumerateFileSystemEntriesAsync(FPath basePath, bool recursive, Predicate<string>? filter)
        {
            return _inner.EnumerateFileSystemEntriesAsync(basePath, recursive, x =>
            {
                var listed = filter == null || filter(x);

                if (listed)
                {
                    Folders.Add(x);
                }

                return listed;
            });
        }

        public ValueTask<FileMetadata> GetFileMetadataAsync(FPath path) => _inner.GetFileMetadataAsync(path);
        public ValueTask<Stream> OpenFileAsync(FPath path) => _inner.OpenFileAsync(path);
        public ValueTask<FileEntry> GetFileEntryAsync(FPath path) => _inner.GetFileEntryAsync(path);
        public ValueTask<DirectoryEntry> GetDirectoryEntryAsync(FPath path) => _inner.GetDirectoryEntryAsync(path);
        public ValueTask<Stream> CreateFileAsync(FPath path) => _inner.CreateFileAsync(path);
        public ValueTask CreateDirectoryAsync(FPath path) => _inner.CreateDirectoryAsync(path);
        public ValueTask DeleteFileAsync(FPath path) => _inner.DeleteFileAsync(path);
        public ValueTask<bool> DeleteDirectoryIfEmptyAsync(FPath path) => _inner.DeleteDirectoryIfEmptyAsync(path);
    }

    private sealed class VanishingFileSystem : IFileSystem
    {
        public MemoryFileSystem Inner { get; } = new();

        // Listed, but not there.
        public HashSet<FPath> Vanished { get; } = [];

        // There, but their stat fails.
        public HashSet<FPath> Failing { get; } = [];

        public async ValueTask<IEnumerable<FileSystemEntry>> EnumerateFileSystemEntriesAsync(FPath basePath, bool recursive, Predicate<string>? filter)
        {
            return [.. await Inner.EnumerateFileSystemEntriesAsync(basePath, recursive, filter), .. Vanished.Select(x => new FileEntry(x, this))];
        }

        public ValueTask<FileMetadata> GetFileMetadataAsync(FPath path)
        {
            return Failing.Contains(path) ? throw new FileNotFoundException(path.FullName) : Inner.GetFileMetadataAsync(path);
        }

        public ValueTask<Stream> OpenFileAsync(FPath path) => Inner.OpenFileAsync(path);
        public ValueTask<FileEntry> GetFileEntryAsync(FPath path) => Inner.GetFileEntryAsync(path);
        public ValueTask<DirectoryEntry> GetDirectoryEntryAsync(FPath path) => Inner.GetDirectoryEntryAsync(path);
        public ValueTask<Stream> CreateFileAsync(FPath path) => Inner.CreateFileAsync(path);
        public ValueTask CreateDirectoryAsync(FPath path) => Inner.CreateDirectoryAsync(path);
        public ValueTask DeleteFileAsync(FPath path) => Inner.DeleteFileAsync(path);
        public ValueTask<bool> DeleteDirectoryIfEmptyAsync(FPath path) => Inner.DeleteDirectoryIfEmptyAsync(path);
    }
}
