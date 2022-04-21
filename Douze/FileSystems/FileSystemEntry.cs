namespace Douze.FileSystems
{
    public abstract class FileSystemEntry : IEquatable<FileSystemEntry>
    {
        public abstract bool Exists { get; }
        public abstract bool IsDirectory { get; }
        public abstract IFileSystem FileSystem { get; }
        public FPath Path { get; internal set; } = FPath.Empty;

        public bool Equals(FileSystemEntry? other)
        {
            if (other == null)
            {
                return false;
            }

            if (this == other)
            {
                return true;
            }

            return Path.Equals(other.Path);
        }

        public override string ToString()
        {
            return Path.FullName;
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as FileSystemEntry);
        }

        public override int GetHashCode()
        {
            return Path.GetHashCode();
        }
    }
}
