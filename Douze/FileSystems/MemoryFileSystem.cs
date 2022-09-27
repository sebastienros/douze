using System.Collections.Concurrent;

namespace Douze.FileSystems;

internal sealed class MemoryFileSystem : IFileSystem
{
    private readonly ConcurrentDictionary<FPath, FileSystemEntry> _files = new();
    private readonly ConcurrentDictionary<FPath, MemoryFileStream> _streams = new();

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

        if (!_streams.TryGetValue(path, out var stream))
        {
            stream = new MemoryFileStream();

            _streams[path] = stream;
        }

        return stream;
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

        if (!_files.ContainsKey(path))
        {
            throw new FileNotFoundException(path.FullName);
        }

        if (!_streams.TryGetValue(path, out var stream))
        {
            stream = new MemoryFileStream();

            _streams[path] = stream;
        }

        return new(stream);
    }

    public ValueTask<FileMetadata> GetFileMetadataAsync(FPath path)
    {
        path = path.ToAbsolute();

        if (!_files.ContainsKey(path))
        {
            throw new FileNotFoundException(path.FullName);
        }

        if (!_streams.TryGetValue(path, out var stream))
        {
            throw new FileNotFoundException(path.FullName);
        }

        return new(stream.Metadata);
    }
}
