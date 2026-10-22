using System.Text;
using Douze.FileSystems;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow;

public class FileContentTests
{
    [Fact]
    public async Task TextBytesAreReadTheSameByEveryThread()
    {
        // The encoded bytes of a text are published once: a reader that sees them sees all of them (on ARM64, a
        // field holding a struct can be seen half written).
        var ct = TestContext.Current.CancellationToken;
        var text = new string('x', 100_000);

        for (var round = 0; round < 100; round++)
        {
            var content = FileContent.FromText(text);

            await Task.WhenAll(Enumerable.Range(0, 8).Select(reader => Task.Run(async () =>
            {
                if (reader % 2 == 0)
                {
                    var bytes = await content.ReadBytesAsync(ct);
                    Assert.Equal(text.Length, bytes.Length);
                    Assert.True(bytes.Span.IndexOfAnyExcept((byte)'x') < 0);
                }
                else
                {
                    using var rented = await content.RentBytesAsync(ct);
                    Assert.Equal(text.Length, rented.Memory.Length);
                    Assert.True(rented.Memory.Span.IndexOfAnyExcept((byte)'x') < 0);
                }
            }, ct)));
        }
    }

    [Fact]
    public async Task LazyFileIsReadOnce()
    {
        var fs = new CountingFileSystem();
        await fs.WriteTextFileAsync("/a.md", "hello");

        var content = FileContent.FromFile(fs, "/a.md", 5);

        Assert.Equal(0, fs.Opens);
        Assert.Null(content.Length);

        Assert.Equal("hello", await content.ReadTextAsync(TestContext.Current.CancellationToken));
        Assert.Equal("hello", await content.ReadTextAsync(TestContext.Current.CancellationToken));
        Assert.Equal(5, (await content.ReadBytesAsync(TestContext.Current.CancellationToken)).Length);
        _ = content.Hash;

        Assert.Equal(1, fs.Opens);
        Assert.Equal(5, content.Length);
    }

    [Fact]
    public async Task LoadAsyncReadsOnceAndMakesTheHashImmediate()
    {
        var fs = new CountingFileSystem();
        await fs.WriteTextFileAsync("/a.md", "hello");

        var content = FileContent.FromFile(fs, "/a.md", 5);

        await content.LoadAsync(TestContext.Current.CancellationToken);
        await content.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, fs.Opens);
        Assert.Equal(5, content.Length);
        Assert.Equal(FileContent.FromText("hello").Hash, content.Hash);
        Assert.Equal(1, fs.Opens);

        // Content that is already in memory has nothing to load.
        await FileContent.FromText("text").LoadAsync(TestContext.Current.CancellationToken);
        await FileContent.FromBytes(new byte[] { 1 }, isText: false).LoadAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LazyTextHashEqualsFromTextHash()
    {
        var fs = new MemoryFileSystem();
        await fs.WriteTextFileAsync("/a.md", "# Title\n\nBody 世界");

        var lazy = FileContent.FromFile(fs, "/a.md", 0);
        var text = FileContent.FromText("# Title\n\nBody 世界");

        Assert.True(lazy.IsText);
        Assert.Equal(text.Hash, lazy.Hash);
    }

    [Fact]
    public async Task ByteOrderMarkIsStripped()
    {
        var bytes = Encoding.UTF8.Preamble.ToArray().Concat(Encoding.UTF8.GetBytes("text")).ToArray();

        Assert.Equal("text", await FileContent.FromBytes(bytes, isText: true).ReadTextAsync(TestContext.Current.CancellationToken));
        Assert.Equal("text", await FileContent.FromText("﻿text").ReadTextAsync(TestContext.Current.CancellationToken));

        var fs = new MemoryFileSystem();
        await fs.WriteBinaryFileAsync("/a.html", bytes);

        Assert.Equal("text", await FileContent.FromFile(fs, "/a.html", bytes.Length).ReadTextAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OpenReadBeforeLoadStreams()
    {
        var fs = new CountingFileSystem();
        await fs.WriteBinaryFileAsync("/image.jpg", [1, 2, 3]);

        var content = FileContent.FromFile(fs, "/image.jpg", 3);

        await using (var stream = await content.OpenReadAsync(TestContext.Current.CancellationToken))
        {
            Assert.Same(fs.LastOpened, stream);
        }

        // Streaming did not load the bytes: reading them opens the file again, once.
        Assert.Null(content.Length);
        Assert.Equal(new byte[] { 1, 2, 3 }, (await content.ReadBytesAsync(TestContext.Current.CancellationToken)).ToArray());
        Assert.Equal(2, fs.Opens);
        Assert.False(content.IsText);

        // Once loaded, streams read the retained bytes.
        await using (var stream = await content.OpenReadAsync(TestContext.Current.CancellationToken))
        {
            Assert.NotSame(fs.LastOpened, stream);
        }

        Assert.Equal(2, fs.Opens);
    }

    private sealed class CountingFileSystem : IFileSystem
    {
        private readonly MemoryFileSystem _inner = new();
        private int _opens;

        public int Opens => Volatile.Read(ref _opens);

        public Stream? LastOpened { get; private set; }

        public async ValueTask<Stream> OpenFileAsync(FPath path)
        {
            Interlocked.Increment(ref _opens);
            return LastOpened = await _inner.OpenFileAsync(path);
        }

        public ValueTask<FileEntry> GetFileEntryAsync(FPath path) => _inner.GetFileEntryAsync(path);
        public ValueTask<DirectoryEntry> GetDirectoryEntryAsync(FPath path) => _inner.GetDirectoryEntryAsync(path);
        public ValueTask<IEnumerable<FileSystemEntry>> EnumerateFileSystemEntriesAsync(FPath basePath, bool recursive, Predicate<string>? filter) => _inner.EnumerateFileSystemEntriesAsync(basePath, recursive, filter);
        public ValueTask<Stream> CreateFileAsync(FPath path) => _inner.CreateFileAsync(path);
        public ValueTask CreateDirectoryAsync(FPath path) => _inner.CreateDirectoryAsync(path);
        public ValueTask<FileMetadata> GetFileMetadataAsync(FPath path) => _inner.GetFileMetadataAsync(path);
    }
}
