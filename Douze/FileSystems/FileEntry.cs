namespace Douze.FileSystems
{
    public sealed class FileEntry : FileSystemEntry
    {
        public static readonly FileEntry NotFound = new(FPath.Empty, NullFileSystem.Instance);

        public override bool IsDirectory => false;

        public override IFileSystem FileSystem { get; }

        public override bool Exists => this != NotFound;

        internal FileEntry(FPath path, IFileSystem fileSystem)
        {
            Path = path;
            FileSystem = fileSystem;
        }
    }
}
