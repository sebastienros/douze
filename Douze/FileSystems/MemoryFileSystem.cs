using System.Collections.Concurrent;

namespace Douze.FileSystems;

internal sealed class MemoryFileSystem : IFileSystem
{
    private readonly ConcurrentDictionary<FPath, FileSystemEntry> _files = new();
    private readonly ConcurrentDictionary<FPath, MemoryFile> _contents = new();

    /// <summary>
    /// Creates the directory represented by the <paramref name="path"/>.
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    public ValueTask CreateDirectoryAsync(FPath path)
    {
        path = path.ToAbsolute().GetDirectory();

        if (_files.ContainsKey(path))
        {
            return ValueTask.CompletedTask;
        }

        _files[path] = new DirectoryEntry(path, this);

        var parent = path.GetParentDirectory();

        if (parent != FPath.Empty)
        {
            return CreateDirectoryAsync(parent);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<Stream> CreateFileAsync(FPath path)
    {
        path = path.ToAbsolute();

        var directory = path.GetDirectory();

        if (!_files.ContainsKey(directory))
        {
            await CreateDirectoryAsync(directory.FullName);
        }

        if (!_files.ContainsKey(path))
        {
            _files[path] = new FileEntry(path, this);
        }

        // The file is replaced (truncated) when the stream is disposed; until then readers see the previous content.
        _contents.TryAdd(path, MemoryFile.Empty);

        return new WriteStream(this, path);
    }

    /// <summary>
    /// Sets the modification time of a file, as a tool that preserves times would.
    /// </summary>
    internal void SetLastWriteTimeUtc(FPath path, DateTime lastWriteTimeUtc)
    {
        path = path.ToAbsolute();
        var file = _contents.TryGetValue(path, out var existing) ? existing : throw new FileNotFoundException(path.FullName);
        _contents[path] = file with { LastWriteTimeUtc = lastWriteTimeUtc };
    }

    public ValueTask<IEnumerable<FileSystemEntry>> EnumerateFileSystemEntriesAsync(FPath basePath, bool recursive, Predicate<string>? filter)
    {
        return new(_files.Values);
    }

    public ValueTask<FileEntry> GetFileEntryAsync(FPath path)
    {
        if (!path.IsFile)
        {
            ThrowArgumentException(path);
        }

        path = path.ToAbsolute();

        _files.TryGetValue(path, out var fileEntry);

        return new(fileEntry as FileEntry ?? FileEntry.NotFound);

        static void ThrowArgumentException(FPath path)
        {
            throw new ArgumentException($"Not a file path: '{path}'");
        }
    }

    public ValueTask<DirectoryEntry> GetDirectoryEntryAsync(FPath path)
    {
        if (!path.IsDirectory)
        {
            ThrowArgumentException(path);
        }

        path = path.ToAbsolute();

        _files.TryGetValue(path, out var directoryEntry);

        return new(directoryEntry as DirectoryEntry ?? DirectoryEntry.NotFound);

        static void ThrowArgumentException(FPath path)
        {
            throw new ArgumentException($"Not a directory path: '{path}'");
        }
    }

    public ValueTask<Stream> OpenFileAsync(FPath path)
    {
        path = path.ToAbsolute();

        if (!_files.ContainsKey(path) || !_contents.TryGetValue(path, out var file))
        {
            throw new FileNotFoundException(path.FullName);
        }

        // Each reader has its own stream over the content.
        return new(new MemoryStream(file.Bytes, writable: false));
    }

    public ValueTask<FileMetadata> GetFileMetadataAsync(FPath path)
    {
        path = path.ToAbsolute();

        if (!_files.ContainsKey(path) || !_contents.TryGetValue(path, out var file))
        {
            throw new FileNotFoundException(path.FullName);
        }

        return new(new FileMetadata(file.Bytes.Length, file.LastWriteTimeUtc, file.LastWriteTimeUtc, file.LastWriteTimeUtc));
    }

    public ValueTask DeleteFileAsync(FPath path)
    {
        path = path.ToAbsolute();

        if (_files.TryGetValue(path, out var entry) && entry is FileEntry)
        {
            _files.TryRemove(path, out _);
            _contents.TryRemove(path, out _);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> DeleteDirectoryIfEmptyAsync(FPath path)
    {
        path = path.ToAbsolute().GetDirectory();

        if (path == FPath.Root || !_files.TryGetValue(path, out var entry) || entry is not DirectoryEntry
            || _files.Keys.Any(x => x != path && x.IsInDirectory(path)))
        {
            return new(false);
        }

        return new(_files.TryRemove(path, out _));
    }

    private sealed record MemoryFile(byte[] Bytes, DateTime LastWriteTimeUtc)
    {
        public static readonly MemoryFile Empty = new([], DateTime.UtcNow);
    }

    /// <summary>
    /// Collects what is written and replaces the file's content when disposed.
    /// </summary>
    private sealed class WriteStream(MemoryFileSystem fileSystem, FPath path) : MemoryStream
    {
        private bool _committed;

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_committed)
            {
                _committed = true;
                fileSystem._contents[path] = new MemoryFile(ToArray(), DateTime.UtcNow);
            }

            base.Dispose(disposing);
        }
    }
}
