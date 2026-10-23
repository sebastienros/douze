using System;
using System.IO;
using System.Threading.Tasks;
using Douze.FileSystems;
using Xunit;

namespace Douze.Tests;

public class PhysicalFileSystemTest : FilesystemBaseTest, IDisposable
{
    private readonly string _folder;

    public PhysicalFileSystemTest() : base()
    {
        _folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_folder);
        _fs = new PhysicalFileSystem(_folder);
    }

    [Fact]
    public async Task WritesAgainToADirectoryDeletedBehindItsBack()
    {
        await _fs.WriteFileAsync("a/b/c.html", "1"u8.ToArray());
        Directory.Delete(Path.Combine(_folder, "a"), recursive: true);

        await _fs.WriteFileAsync("a/b/c.html", "2"u8.ToArray());

        Assert.Equal("2"u8.ToArray(), await _fs.ReadFileAsync("a/b/c.html"));
    }

    [Fact]
    public async Task ReadingAMissingFileThrows()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(async () => await _fs.ReadFileAsync("missing.md"));
    }

    public override void Dispose()
    {
        try
        {
            Directory.Delete(_folder, true);
        }
        finally
        {
            base.Dispose();
        }
    }
}