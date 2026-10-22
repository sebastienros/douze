using System.Collections.Concurrent;
using Douze.FileSystems;
using Douze.Progress;
using Douze.Tracing;
using Douze.Workflow;
using Douze.Workflow.Watch;
using Xunit;
using static Douze.Tests.Workflow.Cache.CacheTestBase;

namespace Douze.Tests.Workflow.Watch;

/// <summary>
/// A watch session over a memory file system: an <see cref="IncrementalBuilder"/> writing to its own output, with its
/// own disk cache in a temporary folder, deleted when disposed unless the test gave the folder. The test reports the
/// changes it makes, as the watcher would.
/// </summary>
internal sealed class TestSession : IDisposable
{
    private readonly string _cacheDirectory;
    private readonly bool _ownsCacheDirectory;

    public TestSession(MemoryFileSystem source, bool useCache = true, Func<SiteOptions, WorkflowDefinition>? workflow = null, FailurePolicy failure = FailurePolicy.Default, string? cacheDirectory = null)
    {
        Source = source;
        _ownsCacheDirectory = cacheDirectory == null;
        _cacheDirectory = cacheDirectory ?? Path.Combine(Path.GetTempPath(), "douze-tests", Guid.NewGuid().ToString("N"));

        var options = new BuildOptions
        {
            MaxDegreeOfParallelism = 4,
            Failure = failure,
            KeepFiles = true,
            CacheDirectory = _cacheDirectory,
            UseCache = useCache,
            Trace = NullTraceRecorder.Instance,
        };

        Builder = new IncrementalBuilder(async () =>
        {
            var site = await SiteOptions.DetectAsync(Source, Output, null, null);
            return ((workflow ?? DefaultWorkflow.Create)(site), site);
        }, options, new MessageView(Messages));
    }

    public MemoryFileSystem Source { get; }

    /// <summary>
    /// The cache folder of the session (<c>--cache-dir</c>).
    /// </summary>
    public string CacheDirectory => _cacheDirectory;

    public MemoryFileSystem Output { get; } = new();

    public IncrementalBuilder Builder { get; }

    public ConcurrentQueue<(LogLevel Level, string Message)> Messages { get; } = new();

    /// <summary>
    /// Builds after <paramref name="changes"/> (the first build when null).
    /// </summary>
    public async Task<RebuildResult> BuildAsync(ChangeSet? changes = null)
    {
        var result = await Builder.BuildAsync(changes, new MessageView(Messages), TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return result;
    }

    /// <summary>
    /// Builds after <paramref name="changes"/> and fails the test when the build fails.
    /// </summary>
    public async Task<RebuildResult> BuildSuccessfullyAsync(ChangeSet? changes = null)
    {
        var result = await BuildAsync(changes);
        Assert.True(result.Build.Succeeded, string.Join(Environment.NewLine, result.Build.Errors.Select(x => $"{x}\n{x.Exception}")));
        return result;
    }

    public static ChangeSet Changes(params (FPath Path, ChangeKind Kind)[] changes)
    {
        var set = new ChangeSet();

        foreach (var (path, kind) in changes)
        {
            set.Add(path, kind);
        }

        return set;
    }

    /// <summary>
    /// Every file of a memory file system with the hash of its content, sorted by path.
    /// </summary>
    public static async Task<SortedDictionary<string, Hash128>> SnapshotAsync(MemoryFileSystem fs)
    {
        var files = new SortedDictionary<string, Hash128>(StringComparer.Ordinal);

        foreach (var entry in await fs.EnumerateFileSystemEntriesAsync(FPath.Root, recursive: true, filter: null))
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            await using var stream = await fs.OpenFileAsync(entry.Path);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            files[entry.Path.ToAbsolute().FullName] = Hash128.Of(memory.ToArray());
        }

        return files;
    }

    public void Dispose()
    {
        Builder.Dispose();

        if (!_ownsCacheDirectory)
        {
            return;
        }

        try
        {
            Directory.Delete(_cacheDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
