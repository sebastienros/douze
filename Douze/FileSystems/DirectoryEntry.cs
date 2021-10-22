namespace Douze.FileSystems
{
    public sealed class DirectoryEntry : FileSystemEntry
    {
        public static DirectoryEntry NotFound = new DirectoryEntry(FPath.Empty, NullFileSystem.Instance);

        public override bool IsDirectory => true;

        public override IFileSystem FileSystem { get; }

        public override bool Exists => this != NotFound;

        public DirectoryEntry(FPath path, IFileSystem fileSystem)
        {
            Path = path;
            FileSystem = fileSystem;
        }
    }
}
