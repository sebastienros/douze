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

        var snapshot = await SourceSnapshot.ScanAsync(source, [], 2, TestContext.Current.CancellationToken);
        Assert.Equal(2, snapshot.Count);

        // A file listed as there whose stat fails is gone too.
        source.Failing.Add("/b.md");
        var kinds = await snapshot.ApplyAsync(source, ["/a.md~", "/b.md"]);

        Assert.Equal([("/b.md", ChangeKind.Deleted)], kinds.Select(x => (x.Path.FullName, x.Kind)));
        Assert.Equal(1, snapshot.Count);
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
