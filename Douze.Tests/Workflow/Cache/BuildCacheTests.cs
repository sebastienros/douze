using System.Collections.Concurrent;
using Douze.FileSystems;
using Douze.Nodes;
using Douze.Progress;
using Douze.Workflow;
using Douze.Workflow.Cache;
using Xunit;

namespace Douze.Tests.Workflow.Cache;

public sealed class BuildCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "douze-tests", Guid.NewGuid().ToString("N"), "cache");
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _messages = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private BuildCache Open() => BuildCache.Open(_directory, new CacheTestBase.MessageView(_messages)) ?? throw new InvalidOperationException("The cache is locked");

    private string[] Packs => Directory.GetFiles(Path.Combine(_directory, "packs"));

    /// <summary>
    /// Stores a unit whose output is <paramref name="file"/>, derived from <paramref name="input"/>.
    /// </summary>
    private static async Task<Hash128> PutAsync(BuildCache cache, string unit, VirtualFile input, VirtualFile file, ScopeSet? scope = null, DependencySet? dependencies = null)
    {
        var key = Hash128.OfText(unit);
        var output = await cache.StoreOutputAsync(file, [input], Ct);
        var cached = new CachedUnit("step", unit, default, [output], scope == null ? null : cache.StoreScope(scope));
        cache.Put(key, cached, dependencies ?? DependencySet.Empty);
        return key;
    }

    private static VirtualFile Input(string path) => new(new FPath(path), FileContent.FromText("input"), new Metadata(new NodeObject(), "/"), null, Provenance.Source);

    [Fact]
    public async Task RoundTrip()
    {
        var date = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var shared = new NodeArray(Enumerable.Range(0, 200).Select(i => (Node?)new NodeObject { ["index"] = NodeValue.Create(i), ["name"] = NodeValue.Create($"item {i}") }).ToArray());
        var item = new Metadata(new NodeObject { ["title"] = NodeValue.Create("Item") }, "/items/");

        var own = new NodeObject
        {
            ["title"] = NodeValue.Create("Hello"),
            ["date"] = NodeValue.Create(date),
            ["count"] = NodeValue.Create(3m),
            ["ratio"] = NodeValue.Create(0.5),
            ["draft"] = NodeValue.Create(false),
            ["items"] = shared,
            ["item"] = new NodeValue<Metadata>(item),
            ["none"] = null,
        };

        var input = Input("/a.md");
        var file = input.With("step", path: "/a.html", content: FileContent.FromText("<p>héllo</p>"), metadata: new Metadata(own, "/blog/"));
        var scope = ScopeSet.Empty
            .WithSite(new SiteScope("step:collect", new NodeObject { ["collections"] = new NodeObject { ["all"] = shared } }))
            .WithFolders([new FolderScope("/blog/", new NodeObject { ["layout"] = NodeValue.Create("blog.html") })]);
        var dependencies = new DependencySet(
            [("/_includes/layout.html", Hash128.OfText("layout")), ("/_includes/missing.liquid", DependencySet.Absent)],
            [new Lookup("/blog/", "collections", Hash128.OfText("collections"), new ValueAccess(false, true, [], [(0, ValueAccess.Whole)])), new Lookup("/blog/", "site", Hash128.OfText("site"), null)],
            [("/a.html", 12, date)]);

        Hash128 key;

        using (var cache = Open())
        {
            key = await PutAsync(cache, "/a.md", input, file, scope, dependencies);
            cache.PutSource("/a.md", 5, date, Hash128.OfText("source"));
            cache.Save(succeeded: true);
        }

        using (var cache = Open())
        {
            Assert.True(cache.TryGetUnit(key, out var unit));
            Assert.Equal(("step", "/a.md"), (unit.Step, unit.Unit));
            Assert.Equal(dependencies, cache.GetDependencies(unit));
            Assert.Equal(dependencies.Lookups[0].Access!.Shape, cache.GetDependencies(unit).Lookups[0].Access!.Shape);
            Assert.True(cache.TryGetSourceHash("/a.md", 5, date, out var source));
            Assert.Equal(Hash128.OfText("source"), source);
            Assert.False(cache.TryGetSourceHash("/a.md", 6, date, out _));

            var restored = cache.Restore(unit.Outputs[0], [input], new Provenance("step", [input.Ref]));

            // The content and metadata are read when needed, and are the same: values keep their types.
            Assert.False(restored.Metadata.IsLoaded);
            Assert.Equal(file.Ref, restored.Ref);
            Assert.Equal("<p>héllo</p>", await restored.Content.ReadTextAsync(Ct));
            Assert.Equal(date, restored.Metadata.Own["date"]!.GetValue<DateTime>());
            Assert.Equal(3m, restored.Metadata.Own["count"]!.GetValue<decimal>());
            Assert.Equal("Item", ((NodeValue<Metadata>)restored.Metadata.Own["item"]!).Value.Own["title"]!.GetValue<string>());
            Assert.Equal(file.Metadata.Hash, new Metadata(restored.Metadata.Own, restored.Metadata.Directory).Hash);
            Assert.Equal(file.LastWriteTimeUtc, restored.LastWriteTimeUtc);
            Assert.Equal(file.SourcePath, restored.SourcePath);

            // A large subtree is stored once, and read back as one shared node.
            var scopes = cache.RestoreScope(unit.Scope!, [input]);
            Assert.Equal(scope.Hash, scopes.Hash);
            Assert.Same(restored.Metadata.Own["items"], ((NodeObject)scopes.Site[0].Values["collections"]!)["all"]);
            Assert.Equal("blog.html", scopes.Folders["/blog/"].Values["layout"]!.GetValue<string>());

            // An output equal to an input is the input.
            var passThrough = await cache.StoreOutputAsync(input, [input], Ct);
            Assert.Same(input, cache.Restore(passThrough, [input], new Provenance("step", [input.Ref])));
        }
    }

    [Fact]
    public async Task BlobsAreVisibleOnlyOnceTheIndexIsSaved()
    {
        Hash128 key;

        // A build that stops before saving leaves a pack that nothing references.
        using (var cache = Open())
        {
            key = await PutAsync(cache, "/a.md", Input("/a.md"), Input("/a.md").With("step", content: FileContent.FromText("output")));
            Assert.Single(Packs);
        }

        using (var cache = Open())
        {
            Assert.False(cache.TryGetUnit(key, out _));
            Assert.Empty(Packs);
        }

        // The index is replaced, never written in place.
        using (var cache = Open())
        {
            await PutAsync(cache, "/a.md", Input("/a.md"), Input("/a.md").With("step", content: FileContent.FromText("output")));
            cache.Save(succeeded: true);
        }

        Assert.Equal(["index.bin", "lock"], Directory.GetFiles(_directory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task VersionMismatchDiscards()
    {
        using (var cache = Open())
        {
            await PutAsync(cache, "/a.md", Input("/a.md"), Input("/a.md").With("step", content: FileContent.FromText("output")));
            cache.Save(succeeded: true);
        }

        // An index written by another version of douze.
        var index = new CacheIndex();
        index.Units[Hash128.OfText("unit")] = new CachedUnit("step", "/a.md", Hash128.Empty, [], null);
        index.Dependencies[Hash128.Empty] = DependencySet.Empty;

        using (var stream = File.Create(Path.Combine(_directory, "index.bin")))
        {
            index.Write(stream, "0.0.1-other");
        }

        using (var cache = Open())
        {
            Assert.Equal(0, cache.PreviousUnits);
            Assert.Empty(Packs);
        }

        Assert.Contains(_messages, x => x.Level == LogLevel.Info && x.Message.Contains("another version of douze"));
    }

    [Fact]
    public async Task Prune()
    {
        var a = Input("/a.md");
        var b = Input("/b.md");
        Hash128 keyA, keyB;

        // B's output is large: once nothing uses it, the pack is mostly unused and is compacted.
        using (var cache = Open())
        {
            keyA = await PutAsync(cache, "/a.md", a, a.With("step", content: FileContent.FromText("small")));
            keyB = await PutAsync(cache, "/b.md", b, b.With("step", content: FileContent.FromText(new string('b', 6 << 20))));
            cache.Save(succeeded: true);
        }

        var packLength = new FileInfo(Assert.Single(Packs)).Length;

        // A failed build keeps the units it did not use.
        using (var cache = Open())
        {
            Assert.True(cache.TryGetUnit(keyA, out var unit));
            cache.Reused(keyA, unit);
            cache.PutSource("/a.md", 1, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), Hash128.Empty);
            cache.Save(succeeded: false);
        }

        using (var cache = Open())
        {
            Assert.True(cache.TryGetUnit(keyB, out _));

            // A successful build keeps only the units it used.
            Assert.True(cache.TryGetUnit(keyA, out var unit));
            cache.Reused(keyA, unit);
            cache.Save(succeeded: true);
        }

        using (var cache = Open())
        {
            Assert.True(cache.TryGetUnit(keyA, out var unit));
            Assert.False(cache.TryGetUnit(keyB, out _));
            Assert.Equal("small", await cache.Restore(unit.Outputs[0], [a], new Provenance("step", [a.Ref])).Content.ReadTextAsync(Ct));
        }

        Assert.True(new FileInfo(Assert.Single(Packs)).Length < packLength / 100);
    }

    [Fact]
    public async Task CorruptIndexIgnoredWithAWarning()
    {
        using (var cache = Open())
        {
            await PutAsync(cache, "/a.md", Input("/a.md"), Input("/a.md").With("step", content: FileContent.FromText("output")));
            cache.Save(succeeded: true);
        }

        var path = Path.Combine(_directory, "index.bin");
        var bytes = await File.ReadAllBytesAsync(path, Ct);
        await File.WriteAllBytesAsync(path, bytes[..(bytes.Length / 2)], Ct);

        using (var cache = Open())
        {
            Assert.Equal(0, cache.PreviousUnits);
            Assert.Empty(Packs);
        }

        Assert.Contains(_messages, x => x.Level == LogLevel.Warning && x.Message.Contains("is damaged and was ignored"));
    }

    [Fact]
    public void ACacheInUseIsNotShared()
    {
        using var first = Open();

        Assert.Null(BuildCache.Open(_directory, new CacheTestBase.MessageView(_messages)));
        Assert.Contains(_messages, x => x.Level == LogLevel.Warning && x.Message.Contains("is used by another build"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_directory)!, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
