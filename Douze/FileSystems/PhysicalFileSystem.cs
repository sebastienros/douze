namespace Douze.FileSystems;

internal sealed class PhysicalFileSystem : IFileSystem
{
    /// <summary>
    /// The base path the file system is rooted on.
    /// </summary>
    private readonly FPath _basePathWithoutTrailingSeparator;

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

        if (filter == null || filter(localPath))
        {
            // localPath is normalized since it can contain backslashes
            yield return new DirectoryEntry(new FPath(localPath, true), this);
        }

        foreach (var file in Directory.GetFiles(path))
        {
            if (filter == null || filter(file))
            {
                yield return new FileEntry(new FPath(file[_basePathWithoutTrailingSeparator.FullName.Length..], true), this);
            }
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
            return new(true);
        }
        catch (IOException)
        {
            // Not empty anymore.
            return new(false);
        }
    }
}
