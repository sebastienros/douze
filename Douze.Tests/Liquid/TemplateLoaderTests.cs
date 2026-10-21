using Douze.FileSystems;
using Douze.Liquid;
using Douze.Nodes;
using Douze.Workflow;
using Fluid;
using Xunit;

namespace Douze.Tests.Liquid;

public class TemplateLoaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TemplateLoader Loader(IFileSystem source) => new(source, ["/_includes/"], LiquidOptions.CreateParser());

    private static TemplateContext Context(TemplateLoader loader, DependencyTracker deps, NodeObject? own = null)
    {
        var context = new TemplateContext(new MetadataValue(new Metadata(own ?? new NodeObject(), "/")), LiquidOptions.Create(loader));
        context.AmbientValues[AmbientKeys.Scopes] = ScopeSet.Empty;
        context.AmbientValues[AmbientKeys.Dependencies] = deps;
        return context;
    }

    [Fact]
    public async Task IncludeResolvesThroughTheIncludesFolderOnceAndRecordsTheDependency()
    {
        var fs = new CountingFileSystem();
        await fs.WriteTextFileAsync("/_includes/header.liquid", "Header {{ title }}");
        await fs.WriteTextFileAsync("/_includes/footer.html", "Footer");

        var loader = Loader(fs);
        var template = loader.GetOrParse(Hash128.OfText("page"), "{% include 'header' %} {% include 'footer' %}", "/page.html");
        var headerHash = Hash128.OfText("Header {{ title }}");

        for (var i = 0; i < 3; i++)
        {
            var deps = new DependencyTracker();
            var rendered = await template.Template.RenderAsync(Context(loader, deps, new NodeObject { ["title"] = NodeValue.Create("Hi") }));

            Assert.Equal("Header Hi Footer", rendered);
            Assert.Equal([("/_includes/footer.html", Hash128.OfText("Footer")), ("/_includes/header.liquid", headerHash)], deps.ToSet().Templates.Select(x => (x.Path.FullName, x.Hash)));
        }

        // Each include file was read once, whatever the number of renders.
        Assert.Equal(2, fs.Opens);

        var missing = loader.GetOrParse(Hash128.OfText("missing"), "{% include 'nope' %}", "/page.html");
        var e = await Assert.ThrowsAsync<DouzeException>(async () => await missing.Template.RenderAsync(Context(loader, new DependencyTracker())));
        Assert.Equal("include 'nope' not found in '/_includes/'", e.Message);
    }

    [Fact]
    public async Task LoadAsyncStripsFrontMatterAndParsesOncePerContent()
    {
        var fs = new CountingFileSystem();
        await fs.WriteTextFileAsync("/_includes/layout.html", "---\nlayout: base.html\n---\n<main>{{ content }}</main>");

        var loader = Loader(fs);
        var deps = new DependencyTracker();

        var first = await loader.LoadAsync("/layout.html", deps, Ct);
        var second = await loader.LoadAsync("layout.html", new DependencyTracker(), Ct);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal("/_includes/layout.html", first.Path.FullName);
        Assert.Equal("base.html", first.FrontMatter["layout"]!.GetValue<string>());
        Assert.Equal(Hash128.OfText("<main>{{ content }}</main>"), first.Template.Hash);
        Assert.Equal([("/_includes/layout.html", first.ContentHash)], deps.ToSet().Templates.Select(x => (x.Path.FullName, x.Hash)));
        Assert.Equal(1, fs.Opens);

        Assert.Null(await loader.LoadAsync("other.html", deps, Ct));
    }

    [Fact]
    public void ParseErrorNamesTheFileOfEachCaller()
    {
        var loader = Loader(new MemoryFileSystem());
        const string broken = "{% if %}";
        var hash = Hash128.OfText(broken);

        var first = Assert.Throws<DouzeException>(() => loader.GetOrParse(hash, broken, "/a.html"));
        var second = Assert.Throws<DouzeException>(() => loader.GetOrParse(hash, broken, "/b.html"));

        // The same content is parsed once; each file reports the error under its own path, with the position.
        Assert.StartsWith("/a.html(1,", first.Message);
        Assert.StartsWith("/b.html(1,", second.Message);
        Assert.Equal(first.Message["/a.html".Length..], second.Message["/b.html".Length..]);
    }

    private sealed class CountingFileSystem : IFileSystem
    {
        private readonly MemoryFileSystem _inner = new();
        private int _opens;

        public int Opens => Volatile.Read(ref _opens);

        public ValueTask<Stream> OpenFileAsync(FPath path)
        {
            Interlocked.Increment(ref _opens);
            return _inner.OpenFileAsync(path);
        }

        public ValueTask<FileEntry> GetFileEntryAsync(FPath path) => _inner.GetFileEntryAsync(path);
        public ValueTask<DirectoryEntry> GetDirectoryEntryAsync(FPath path) => _inner.GetDirectoryEntryAsync(path);
        public ValueTask<IEnumerable<FileSystemEntry>> EnumerateFileSystemEntriesAsync(FPath basePath, bool recursive, Predicate<string>? filter) => _inner.EnumerateFileSystemEntriesAsync(basePath, recursive, filter);
        public ValueTask<Stream> CreateFileAsync(FPath path) => _inner.CreateFileAsync(path);
        public ValueTask CreateDirectoryAsync(FPath path) => _inner.CreateDirectoryAsync(path);
        public ValueTask<FileMetadata> GetFileMetadataAsync(FPath path) => _inner.GetFileMetadataAsync(path);
    }
}
