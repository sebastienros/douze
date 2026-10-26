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
    public async Task RestoreWithAnIndexMatchesTheScan()
    {
        // A unit with many inputs and outputs (a batch step) restores its outputs from an index of its inputs.
        var inputs = Enumerable.Range(0, 40).Select(i => new VirtualFile(new FPath($"/{i}.md"), FileContent.FromText($"content {i % 20}"), new Metadata(new NodeObject { ["n"] = NodeValue.Create(i % 10) }, "/"), null, Provenance.Source)).ToArray();
        var files = new[]
        {
            inputs[25],
            inputs[3].With("step", path: "/3.html"),
            inputs[4].With("step", path: "/4.html", metadata: new Metadata(new NodeObject { ["n"] = NodeValue.Create(99) }, "/")),
            inputs[5].With("step", path: "/5.html", content: FileContent.FromText("new")),
        };

        using var cache = Open();
        var outputs = new List<CachedOutput>();

        foreach (var file in files)
        {
            outputs.Add(await cache.StoreOutputAsync(file, inputs, Ct));
        }

        var provenance = new Provenance("step", [.. inputs.Select(x => x.Ref)]);
        var index = new BuildCache.RestoreIndex(inputs);

        foreach (var (output, file) in outputs.Zip(files))
        {
            var scanned = cache.Restore(output, inputs, provenance);
            var indexed = cache.Restore(output, index, provenance);

            Assert.Equal(file.Ref, indexed.Ref);
            Assert.Equal(scanned.Ref, indexed.Ref);
            Assert.Equal(await scanned.Content.ReadTextAsync(Ct), await indexed.Content.ReadTextAsync(Ct));
        }

        // An output equal to an input is that input; equal content or metadata are the first input's that has them.
        Assert.Same(inputs[25], cache.Restore(outputs[0], index, provenance));
        Assert.Same(inputs[3].Content, cache.Restore(outputs[1], index, provenance).Content);
        Assert.Same(inputs[4].Content, cache.Restore(outputs[2], index, provenance).Content);
        Assert.DoesNotContain(cache.Restore(outputs[2], index, provenance).Metadata, inputs.Select(x => x.Metadata));
        Assert.Same(inputs[5].Metadata, cache.Restore(outputs[3], index, provenance).Metadata);
    }

    [Fact]
    public async Task ReadsAfterDisposeLeaveNoPackOpen()
    {
        var input = Input("/a.md");
        Hash128 key;

        using (var cache = Open())
        {
            key = await PutAsync(cache, "/a.md", input, input.With("step", path: "/a.html", content: FileContent.FromText("output")));
            cache.Save(succeeded: true);
        }

        VirtualFile restored;

        using (var cache = Open())
        {
            Assert.True(cache.TryGetUnit(key, out var unit));
            restored = cache.Restore(unit.Outputs[0], [input], new Provenance("step", [input.Ref]));
            Assert.Equal("output", await cache.Restore(unit.Outputs[0], [input], new Provenance("step", [input.Ref])).Content.ReadTextAsync(Ct));
        }

        // The content of a replayed file stays readable once the build that restored it closed the cache, without
        // reopening a pack for good: an open file cannot be deleted on Windows. FileShare.None fails on every OS while
        // a handle is open.
        Assert.Equal("output", await restored.Content.ReadTextAsync(Ct));
        Assert.All(Packs, x => File.Open(x, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose());
        Directory.Delete(_directory, recursive: true);
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
    public async Task TruncatedPackDiscardsTheCache()
    {
        using (var cache = Open())
        {
            await PutAsync(cache, "/a.md", Input("/a.md"), Input("/a.md").With("step", content: FileContent.FromText(new string('a', 4096))));
            cache.Save(succeeded: true);
        }

        // A build that stopped while the system was writing the pack (a power loss) leaves it shorter than its blobs.
        var pack = Assert.Single(Packs);
        var bytes = await File.ReadAllBytesAsync(pack, Ct);
        await File.WriteAllBytesAsync(pack, bytes[..(bytes.Length / 2)], Ct);

        using (var cache = Open())
        {
            Assert.Equal(0, cache.PreviousUnits);
            Assert.Empty(Packs);
        }

        Assert.Contains(_messages, x => x.Level == LogLevel.Warning && x.Message.Contains("is shorter than its blobs"));
    }

    [Fact]
    public async Task ABlobIsReadableOnceAnyPutOfItReturns()
    {
        // Units that store the same value at once (the highlight step's memo of a snippet every page has): one writes the
        // blob, and the others must not return before it is written, or a read right after finds no blob.
        using var cache = Open();
        var value = new byte[1024 * 1024];
        new Random(1).NextBytes(value);

        for (var round = 0; round < 50; round++)
        {
            value[0] = (byte)round;
            var blob = value.ToArray();

            await Parallel.ForAsync(0, 8, Ct, (unit, _) =>
            {
                var key = Hash128.OfText($"{round} {unit}");
                cache.PutMemo("highlight", key, blob);
                Assert.True(cache.TryGetMemo("highlight", key, out var read));
                Assert.Equal(blob.Length, read.Length);
                return ValueTask.CompletedTask;
            });
        }
    }

    [Fact]
    public void Memos()
    {
        var a = Hash128.OfText("a");
        var b = Hash128.OfText("b");
        var c = Hash128.OfText("c");

        using (var cache = Open())
        {
            Assert.False(cache.TryGetMemo("twoslash", a, out _));
            cache.PutMemo("twoslash", a, "result a"u8);
            cache.PutMemo("twoslash", b, "result b"u8);
            cache.PutMemo("other", c, "result c"u8);

            // Readable in the build that stored them.
            Assert.True(cache.TryGetMemo("twoslash", a, out var value));
            Assert.Equal("result a"u8.ToArray(), value);
            cache.Save(succeeded: true);
        }

        // A build in which the step reads only a keeps a, and the memos of the steps that did not run.
        using (var cache = Open())
        {
            Assert.True(cache.TryGetMemo("twoslash", a, out var value));
            Assert.Equal("result a"u8.ToArray(), value);
            cache.Save(succeeded: true);
        }

        using (var cache = Open())
        {
            Assert.True(cache.TryGetMemo("twoslash", a, out _));
            Assert.False(cache.TryGetMemo("twoslash", b, out _));
            Assert.True(cache.TryGetMemo("other", c, out var value));
            Assert.Equal("result c"u8.ToArray(), value);

            // A build in which no step reads a memo (its units were replayed) keeps them all.
            cache.Save(succeeded: true);
        }

        using (var cache = Open())
        {
            cache.Save(succeeded: true);
        }

        using (var cache = Open())
        {
            Assert.True(cache.TryGetMemo("twoslash", a, out _));
            Assert.True(cache.TryGetMemo("other", c, out _));
        }
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
