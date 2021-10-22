using Douze.FileSystems;

namespace Douze.Tests
{
    public class MemoryFileSystemTest : FilesystemBaseTest
    {
        public MemoryFileSystemTest() : base(new MemoryFileSystem())
        {
        }
    }
}
