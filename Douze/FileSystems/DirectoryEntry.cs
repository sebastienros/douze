namespace Douze.FileSystems;

public sealed class DirectoryEntry : FileSystemEntry
{
    public static readonly DirectoryEntry NotFound = new(FPath.Empty, NullFileSystem.Instance);

    public override bool IsDirectory => true;

    public override IFileSystem FileSystem { get; }

    public override bool Exists => this != NotFound;

    internal DirectoryEntry(FPath path, IFileSystem fileSystem)
    {
        Path = path;
        FileSystem = fileSystem;
    }
}