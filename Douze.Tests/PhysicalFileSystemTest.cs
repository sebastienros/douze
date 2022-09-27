using System;
using System.IO;
using Douze.FileSystems;

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