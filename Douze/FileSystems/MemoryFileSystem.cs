using System.Collections.Concurrent;

namespace Douze.FileSystems
{
    internal class MemoryFileSystem : IFileSystem
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
            path = FPath.Combine(FPath.Root, path);
            var key = path.ToDirectory();

            if (_files.ContainsKey(path))
            {
                return ValueTask.CompletedTask;
            }

            _files[key] = new DirectoryEntry(FPath.Combine(FPath.Root, path), this);

            var parent = FileSystemHelper.GetParentDirectory(path);

            if (parent != FPath.Empty)
            {
                CreateDirectoryAsync(parent);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<Stream> CreateFileAsync(FPath path)
        {
            path = path.ToAbsolute();

            var directory = path.GetDirectory();
            var directoryKey = FPath.Combine(FPath.Root, directory).ToDirectory();

            if (!_files.ContainsKey(directoryKey))
            {
                throw new DirectoryNotFoundException(path.FullName);
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

            return new(stream);
        }

        public ValueTask<IEnumerable<FileSystemEntry>> EnumerateFileSystemEntriesAsync(FPath basePath, bool recursive, Predicate<string>? filter)
        {
            return new(_files.Values);
        }

        public ValueTask<FileEntry> GetFileEntryAsync(FPath path)
        {
            path = path.ToAbsolute();

            _files.TryGetValue(path, out var fileEntry);

            return new(fileEntry as FileEntry ?? FileEntry.NotFound);
        }

        public ValueTask<DirectoryEntry> GetDirectoryEntryAsync(FPath path)
        {
            var key = path.ToAbsolute().ToDirectory();

            _files.TryGetValue(key, out var directoryEntry);

            return new(directoryEntry as DirectoryEntry ?? DirectoryEntry.NotFound);
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
    }
}
