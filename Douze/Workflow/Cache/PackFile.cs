using Microsoft.Win32.SafeHandles;

namespace Douze.Workflow.Cache;

/// <summary>
/// Where a blob is: its pack, its offset and its length.
/// </summary>
internal readonly record struct BlobLocation(int Pack, long Offset, int Length);

/// <summary>
/// An append-only file of blobs. A build writes the blobs of the units it runs to a new pack, then the index makes them
/// visible: a build that stops before saving its index leaves a pack nobody references, deleted by the next build.
/// Thousands of small files would cost a file creation each, which is what limits writing a site (benchmark finding 4).
/// </summary>
internal sealed class PackFile : IDisposable
{
    private readonly Lock _lock = new();
    private readonly SafeFileHandle _handle;
    private readonly bool _writable;
    private FileStream? _stream;
    private long _length;

    private PackFile(string path, SafeFileHandle handle, bool writable, long length)
    {
        Path = path;
        _handle = handle;
        _writable = writable;
        _length = length;

        if (writable)
        {
            _stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 1 << 20);
        }
    }

    public string Path { get; }

    public long Length => _length;

    public static PackFile OpenRead(string path)
    {
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new PackFile(path, handle, writable: false, RandomAccess.GetLength(handle));
    }

    public static PackFile Create(string path)
    {
        var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        return new PackFile(path, handle, writable: true, 0);
    }

    /// <summary>
    /// Appends a blob and returns its offset.
    /// </summary>
    public long Append(ReadOnlySpan<byte> data)
    {
        if (!_writable)
        {
            throw new InvalidOperationException("The pack is read-only");
        }

        lock (_lock)
        {
            var offset = _length;
            _stream!.Write(data);
            _length += data.Length;
            return offset;
        }
    }

    public byte[] Read(long offset, int length)
    {
        if (_writable)
        {
            // A blob written by this build may still be in the stream's buffer.
            lock (_lock)
            {
                _stream!.Flush();
            }
        }

        var buffer = GC.AllocateUninitializedArray<byte>(length);
        var read = 0;

        while (read < length)
        {
            var count = RandomAccess.Read(_handle, buffer.AsSpan(read), offset + read);

            if (count == 0)
            {
                throw new InvalidDataException($"The cache pack '{Path}' ends before the blob at {offset}");
            }

            read += count;
        }

        return buffer;
    }

    public void Flush()
    {
        lock (_lock)
        {
            _stream?.Flush();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _stream?.Dispose();
            _stream = null;
            _handle.Dispose();
        }
    }
}
