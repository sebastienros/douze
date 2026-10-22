using System.Collections.Concurrent;
using Douze.FileSystems;
using Douze.Tracing;
using Douze.Workflow;
using Douze.Workflow.Cache;
using Xunit;
using static Douze.Tests.Workflow.Cache.CacheTestBase;

namespace Douze.Tests.Workflow.Watch;

/// <summary>
/// The disk cache kept open by a watch session: committed in memory after each build, written once.
/// </summary>
public sealed class SessionCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "douze-tests", Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<(Douze.Progress.LogLevel, string)> _messages = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EachBuildTrustsTheFilesChangedBeforeItsOwnStart()
    {
        var clock = new Clock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var source = new CountingFileSystem();
        var output = new MemoryFileSystem();
        await source.WriteTextFileAsync("/index.md", "---\ntitle: Home\n---\nHome");

        // Changed a second before the session starts: too recent to trust its stat.
        source.Inner.SetLastWriteTimeUtc("/index.md", clock.Now.UtcDateTime.AddSeconds(-1));

        using var cache = BuildCache.Open(Path.Combine(_directory, "cache"), new MessageView(_messages), clock)!;
        var site = await SiteOptions.DetectAsync(source, output, null, null);

        async Task<BuildResult> BuildAsync()
        {
            var result = await SiteBuilder.BuildAsync(DefaultWorkflow.Create(site), site, new BuildOptions
            {
                MaxDegreeOfParallelism = 2,
                Cache = cache,
                CacheDirectory = _directory,
                Trace = NullTraceRecorder.Instance,
                Cancellation = Ct,
            });

            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));
            return result;
        }

        await BuildAsync();
        Assert.Equal(1, source.Opens("/index.md"));

        // Still within 2 s of the build start: read again to be hashed.
        await BuildAsync();
        Assert.Equal(2, source.Opens("/index.md"));

        // A build later in the session: the file is older than its start by more than the window, its stat is trusted
        // from now on. With the start of the session as the reference, every build would read it again.
        clock.Now = clock.Now.AddMinutes(1);
        await BuildAsync();
        Assert.Equal(3, source.Opens("/index.md"));

        var last = await BuildAsync();
        Assert.Equal(3, source.Opens("/index.md"));
        Assert.Equal(0, last.UnitsExecuted);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// A memory file system that counts the files opened, by path.
    /// </summary>
    private sealed class CountingFileSystem : IFileSystem
    {
        private readonly ConcurrentDictionary<FPath, int> _opens = new();

        public MemoryFileSystem Inner { get; } = new();

        public int Opens(FPath path) => _opens.GetValueOrDefault(path);

        public ValueTask<Stream> OpenFileAsync(FPath path)
        {
            _opens.AddOrUpdate(path.ToAbsolute(), 1, (_, x) => x + 1);
            return Inner.OpenFileAsync(path);
        }

        public ValueTask<FileEntry> GetFileEntryAsync(FPath path) => Inner.GetFileEntryAsync(path);
        public ValueTask<DirectoryEntry> GetDirectoryEntryAsync(FPath path) => Inner.GetDirectoryEntryAsync(path);
        public ValueTask<IEnumerable<FileSystemEntry>> EnumerateFileSystemEntriesAsync(FPath basePath, bool recursive, Predicate<string>? filter) => Inner.EnumerateFileSystemEntriesAsync(basePath, recursive, filter);
        public ValueTask<Stream> CreateFileAsync(FPath path) => Inner.CreateFileAsync(path);
        public ValueTask CreateDirectoryAsync(FPath path) => Inner.CreateDirectoryAsync(path);
        public ValueTask<FileMetadata> GetFileMetadataAsync(FPath path) => Inner.GetFileMetadataAsync(path);
        public ValueTask DeleteFileAsync(FPath path) => Inner.DeleteFileAsync(path);
        public ValueTask<bool> DeleteDirectoryIfEmptyAsync(FPath path) => Inner.DeleteDirectoryIfEmptyAsync(path);
    }
}
