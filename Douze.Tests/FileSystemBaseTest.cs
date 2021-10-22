using System;
using System.Linq;
using System.Threading.Tasks;
using Douze.FileSystems;
using Xunit;

namespace Douze.Tests
{
    public abstract class FilesystemBaseTest : IDisposable
    {
        protected IFileSystem _fs;
        private readonly System.Threading.SemaphoreSlim _semaphore = new(1);

        public FilesystemBaseTest()
        {
            _fs = new MemoryFileSystem();
            _semaphore.Wait();
        }

        public FilesystemBaseTest(IFileSystem fs)
        {
            _fs = fs;
            _semaphore.Wait();
        }

        [Fact]
        public async Task ShouldCreateDirectories()
        {
            await _fs.CreateDirectoryAsync("a");

            var entries = await _fs.EnumerateFileSystemEntriesAsync(FPath.Root, true, null);

            Assert.Equal(2, entries.Count());
        }

        [Fact]
        public async Task ShouldCreateFiles()
        {
            await _fs.WriteTextFileAsync("a", "Hello");

            var content = await _fs.ReadAllTextAsync("a");

            Assert.Equal("Hello", content);
        }

        public virtual void Dispose()
        {
            _semaphore.Release();
        }
    }
}
