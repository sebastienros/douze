namespace Douze.FileSystems
{
    public sealed class FileEntry : FileSystemEntry
    {
        public static FileEntry NotFound = new FileEntry(FPath.Empty, NullFileSystem.Instance);

        public override bool IsDirectory => false;

        public override IFileSystem FileSystem { get; }

        public override bool Exists => this != NotFound;

        public FileEntry(FPath path, IFileSystem fileSystem)
        {
            Path = path;
            FileSystem = fileSystem;
        }
    }
}
