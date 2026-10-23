using System.Collections.Concurrent;

namespace Douze.FileSystems;

internal sealed class PhysicalFileSystem : IFileSystem
{
    /// <summary>
    /// The base path the file system is rooted on.
    /// </summary>
    private readonly FPath _basePathWithoutTrailingSeparator;

    /// <summary>
    /// The number of files <see cref="WriteFileAsync"/> writes at once. Creating files and folders contends in the
    /// kernel (a folder's lock, the journal): 4000 pages in their own folders are written faster by 4 threads than by
    /// 14, on Linux and on macOS (<c>bench/README.md</c>).
    /// </summary>
    public const int MaxConcurrentWrites = 4;

    private readonly SemaphoreSlim _writes = new(MaxConcurrentWrites);

    // The directories WriteFileAsync created or found: a site writes many files to one folder, which it checks once.
    private readonly ConcurrentDictionary<string, bool> _directories = new(StringComparer.Ordinal);

    public PhysicalFileSystem(FPath basePath)
    {
        _basePathWithoutTrailingSeparator = new FPath(basePath.FullName.TrimEnd(FPath.DirectorySeparator));
    }

    /// <summary>
    /// Creates the directory represented by the <paramref name="path"/>.
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    public ValueTask CreateDirectoryAsync(FPath path)
    {
        if (!path.IsDirectory)
        {
            ThrowArgumentException(path);
        }

        path = _basePathWithoutTrailingSeparator.FullName + path.ToAbsolute();

        if (!Directory.Exists(path.FullName))
        {
            Directory.CreateDirectory(path.FullName);
        }

        return ValueTask.CompletedTask;

        static void ThrowArgumentException(FPath path)
        {
            throw new ArgumentException($"Not a directory path: '{path}'");
        }
    }

    public ValueTask<Stream> CreateFileAsync(FPath path)
    {
        if (!path.IsFile)
        {
            ThrowArgumentException(path);
        }

        path = new FPath(_basePathWithoutTrailingSeparator.FullName + path.ToAbsolute(), false);

        var directoryName = Path.GetDirectoryName(path.FullName);

        if (directoryName != null)
        {
            if (!Directory.Exists(directoryName))
            {
                Directory.CreateDirectory(directoryName);
            }
        }

        return new(File.Create(path.FullName, 0, FileOptions.Asynchronous));

        static void ThrowArgumentException(FPath path)
        {
            throw new ArgumentException($"Not a file path: '{path}'");
        }
    }

    /// <summary>
    /// Writes the file synchronously, <see cref="MaxConcurrentWrites"/> at a time: an output file is small, and an
    /// asynchronous write on Unix is the same blocking call made from another thread pool item.
    /// </summary>
    public async ValueTask WriteFileAsync(FPath path, ReadOnlyMemory<byte> content)
    {
        if (!path.IsFile)
        {
            throw new ArgumentException($"Not a file path: '{path}'");
        }

        var fullPath = _basePathWithoutTrailingSeparator.FullName + path.ToAbsolute().FullName;
        var directory = Path.GetDirectoryName(fullPath);

        await _writes.WaitAsync();

        try
        {
            if (directory != null && !_directories.ContainsKey(directory))
            {
                Directory.CreateDirectory(directory);
                _directories.TryAdd(directory, true);
            }

            try
            {
                Write(fullPath, content.Span);
            }
            catch (DirectoryNotFoundException) when (directory != null)
            {
                // Deleted since it was created (a watch session deletes the folders of stale outputs).
                Directory.CreateDirectory(directory);
                Write(fullPath, content.Span);
            }
        }
        finally
        {
            _writes.Release();
        }

        static void Write(string path, ReadOnlySpan<byte> content)
        {
            using var handle = File.OpenHandle(path, FileMode.Create, FileAccess.Write);
            RandomAccess.Write(handle, content, 0);
        }
    }

    public ValueTask<IEnumerable<FileSystemEntry>> EnumerateFileSystemEntriesAsync(FPath path, bool recursive, Predicate<string>? filter)
    {
        path = new FPath(_basePathWithoutTrailingSeparator.FullName + path.ToAbsolute(), false);

        return new(EnumerateFileEntries(path.FullName, recursive, filter));
    }

    private IEnumerable<FileSystemEntry> EnumerateFileEntries(string path, bool recursive, Predicate<string>? filter)
    {
        // path should not end with '/'

        path = path.TrimEnd('/');

        if (!Directory.Exists(path))
        {
            yield break;
        }

        var localPath = path[_basePathWithoutTrailingSeparator.FullName.Length..];

        if (filter != null && !filter(localPath.Length == 0 ? "/" : localPath))
        {
            yield break;
        }

        // localPath is normalized since it can contain backslashes
        yield return new DirectoryEntry(new FPath(localPath, true), this);

        foreach (var file in Directory.GetFiles(path))
        {
            yield return new FileEntry(new FPath(file[_basePathWithoutTrailingSeparator.FullName.Length..], true), this);
        }

        if (recursive)
        {
            foreach (var dir in Directory.EnumerateDirectories(path))
            {
                foreach (var entry in EnumerateFileEntries(dir, recursive, filter))
                {
                    yield return entry;
                }
            }
        }
    }

    public ValueTask<FileEntry> GetFileEntryAsync(FPath path)
    {
        if (!path.IsFile)
        {
            ThrowArgumentException(path);
        }

        path = path.ToAbsolute();

        var fullPath = _basePathWithoutTrailingSeparator + path.FullName;

        if (!File.Exists(fullPath))
        {
            return new(FileEntry.NotFound);
        }

        return new(new FileEntry(path, this));

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

        var fullPath = _basePathWithoutTrailingSeparator.FullName + path.ToAbsolute();

        if (!Directory.Exists(fullPath))
        {
            return new(DirectoryEntry.NotFound);
        }

        return new(new DirectoryEntry(path, this));

        static void ThrowArgumentException(FPath path)
        {
            throw new ArgumentException($"Not a directory path: '{path}'");
        }
    }

    public ValueTask<Stream> OpenFileAsync(FPath path)
    {
        var fullPath = _basePathWithoutTrailingSeparator.FullName + path.ToAbsolute();

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(fullPath);
        }

        return new(new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true));
    }

    /// <summary>
    /// Reads the file synchronously: a source file is small, and an asynchronous read on Unix is the same blocking call
    /// made from another thread pool item.
    /// </summary>
    public ValueTask<byte[]> ReadFileAsync(FPath path)
    {
        return new(File.ReadAllBytes(_basePathWithoutTrailingSeparator.FullName + path.ToAbsolute()));
    }

    public ValueTask<FileMetadata> GetFileMetadataAsync(FPath path)
    {
        var fullPath = _basePathWithoutTrailingSeparator.FullName + path.ToAbsolute();

        var fileInfo = new FileInfo(fullPath);
        return new(new FileMetadata(fileInfo.Length, fileInfo.CreationTimeUtc, fileInfo.LastAccessTimeUtc, fileInfo.LastWriteTimeUtc));
    }

    public ValueTask DeleteFileAsync(FPath path)
    {
        File.Delete(_basePathWithoutTrailingSeparator.FullName + path.ToAbsolute());
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> DeleteDirectoryIfEmptyAsync(FPath path)
    {
        var fullPath = (_basePathWithoutTrailingSeparator.FullName + path.ToAbsolute()).TrimEnd('/');

        if (!Directory.Exists(fullPath) || Directory.EnumerateFileSystemEntries(fullPath).Any())
        {
            return new(false);
        }

        try
        {
            Directory.Delete(fullPath, recursive: false);
            _directories.TryRemove(fullPath, out _);
            return new(true);
        }
        catch (IOException)
        {
            // Not empty anymore.
            return new(false);
        }
    }
}
