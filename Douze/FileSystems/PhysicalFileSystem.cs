namespace Douze.FileSystems
{
    internal class PhysicalFileSystem : IFileSystem
    {
        /// <summary>
        /// The base path the file system is rooted on.
        /// </summary>
        private readonly FPath _basePath;

        public PhysicalFileSystem(FPath basePath)
        {
            _basePath = new FPath(basePath.FullName.TrimEnd('/'));
        }

        /// <summary>
        /// Creates the directory represented by the <paramref name="path"/>.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public ValueTask CreateDirectoryAsync(FPath path)
        {
            path = _basePath.FullName + path.ToAbsolute();

            Directory.CreateDirectory(path.FullName);

            return ValueTask.CompletedTask;
        }

        public ValueTask<Stream> CreateFileAsync(FPath path)
        {
            path = new FPath(_basePath.FullName + path.ToAbsolute(), false);

            var directoryName = Path.GetDirectoryName(path.FullName);

            if (directoryName != null)
            {
                Directory.CreateDirectory(directoryName);
            }

            return new(File.Create(path.FullName));
        }

        public ValueTask<IEnumerable<FileSystemEntry>> EnumerateFileSystemEntriesAsync(FPath path, bool recursive, Predicate<string>? filter)
        {
            path = new FPath(_basePath.FullName + path.ToAbsolute(), false);

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

            var localPath = path.Substring(_basePath.FullName.Length);

            if (filter == null || filter(localPath))
            {
                // localPath is normalized since it can containt backslashes
                yield return new DirectoryEntry(new FPath(localPath, true), this);
            }

            foreach (var file in Directory.GetFiles(path))
            {
                if (filter == null || filter(file))
                {
                    yield return new FileEntry(new FPath(file.Substring(_basePath.FullName.Length), true), this);
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
            var fullPath = _basePath.FullName + path.ToAbsolute();

            if (!File.Exists(fullPath))
            {
                return new(FileEntry.NotFound);
            }

            return new(new FileEntry(path, this));
        }

        public ValueTask<DirectoryEntry> GetDirectoryEntryAsync(FPath path)
        {
            var fullPath = _basePath.FullName + path.ToAbsolute();

            if (!Directory.Exists(fullPath))
            {
                return new(DirectoryEntry.NotFound);
            }

            return new(new DirectoryEntry(path, this));
        }

        public ValueTask<Stream> OpenFileAsync(FPath path)
        {
            var fullPath = _basePath.FullName + path.ToAbsolute();

            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException(fullPath);
            }

            return new(new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true));
        }
    }
}
