namespace Douze.FileSystems;

public interface IFileSystem
{
    /// <summary>
    /// Opens the specific file in read-only mode.
    /// </summary>
    ValueTask<Stream> OpenFileAsync(FPath path);

    ValueTask<FileEntry> GetFileEntryAsync(FPath path);

    ValueTask<DirectoryEntry> GetDirectoryEntryAsync(FPath path);

    ValueTask<IEnumerable<FileSystemEntry>> EnumerateFileSystemEntriesAsync(FPath basePath, bool recursive, Predicate<string>? filter);

    /// <summary>
    /// Creates or truncates a file, and its directory when it does not exist.
    /// </summary>
    ValueTask<Stream> CreateFileAsync(FPath path);

    ValueTask CreateDirectoryAsync(FPath path);

    ValueTask<FileMetadata> GetFileMetadataAsync(FPath path);

    /// <summary>
    /// Deletes a file. A file that does not exist is not an error.
    /// </summary>
    ValueTask DeleteFileAsync(FPath path);

    /// <summary>
    /// Deletes a directory when it is empty, and returns whether it did.
    /// </summary>
    ValueTask<bool> DeleteDirectoryIfEmptyAsync(FPath path);
}

public static class FileSystemExtensions
{
    public static async ValueTask WriteTextFileAsync(this IFileSystem fs, FPath path, string content)
    {
        using var stream = await fs.CreateFileAsync(path);
        using var sw = new StreamWriter(stream);
        await sw.WriteAsync(content);
    }

    public static async ValueTask WriteBinaryFileAsync(this IFileSystem fs, FPath path, byte[] content)
    {
        using var stream = await fs.CreateFileAsync(path);
        await stream.WriteAsync(content);
    }

    public static async ValueTask<string> ReadAllTextAsync(this IFileSystem fs, FPath path)
    {
        using var stream = await fs.OpenFileAsync(path);
        using var sw = new StreamReader(stream);
        return await sw.ReadToEndAsync();
    }
}