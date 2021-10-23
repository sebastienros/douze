namespace Douze.FileSystems
{
    internal class NullFileSystem : IFileSystem
    {
        public static readonly NullFileSystem Instance = new NullFileSystem();

        public ValueTask CreateDirectoryAsync(FPath path)
        {
            throw new NotImplementedException();
        }

        public ValueTask<Stream> CreateFileAsync(FPath path)
        {
            throw new NotImplementedException();
        }

        public ValueTask<IEnumerable<FileSystemEntry>> EnumerateFileSystemEntriesAsync(FPath basePath, bool recursive, Predicate<string>? filter)
        {
            throw new NotImplementedException();
        }

        public ValueTask<FileEntry> GetFileEntryAsync(FPath path)
        {
            throw new NotImplementedException();
        }

        public ValueTask<DirectoryEntry> GetDirectoryEntryAsync(FPath path)
        {
            throw new NotImplementedException();
        }

        public ValueTask<Stream> OpenFileAsync(FPath path)
        {
            throw new NotImplementedException();
        }

        public ValueTask<FileMetadata> GetFileMetadaAsync(FPath path)
        {
            throw new NotImplementedException();
        }
    }
}
