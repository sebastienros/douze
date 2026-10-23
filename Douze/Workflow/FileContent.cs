using System.Text;
using Douze.FileSystems;

namespace Douze.Workflow;

/// <summary>
/// The immutable content of a <see cref="VirtualFile"/>: text, bytes, or a source file read on first access.
/// </summary>
internal abstract class FileContent
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".html", ".htm", ".liquid", ".json", ".yml", ".yaml", ".xml", ".txt",
        ".css", ".js", ".mjs", ".cjs", ".ts", ".svg", ".csv", ".tsv", ".map", ".rss", ".atom", ".webmanifest",
    };

    private protected readonly Lock _hashLock = new();
    private protected Hash128 _hash;
    private protected volatile bool _hashed;

    protected FileContent()
    {
    }

    /// <summary>
    /// Content whose hash, when not null, is known without reading it: a blob of the cache, a source file that did not
    /// change.
    /// </summary>
    protected FileContent(Hash128? hash)
    {
        _hash = hash ?? default;
        _hashed = hash != null;
    }

    public static readonly FileContent Empty = new TextContent("");

    public static FileContent FromText(string text) => new TextContent(text);

    public static FileContent FromBytes(ReadOnlyMemory<byte> bytes, bool isText) => new BytesContent(bytes, isText);

    /// <summary>
    /// Creates content that reads the file once, on first access, and then retains its bytes.
    /// </summary>
    public static FileContent FromFile(IFileSystem fs, FPath path) => new LazyFileContent(fs, path, IsTextPath(path), null, null);

    /// <summary>
    /// Creates content that reads the file once, on first access. <paramref name="hash"/> is its hash when known (the
    /// file did not change since a previous build); otherwise <paramref name="hashed"/> receives it once computed.
    /// </summary>
    public static FileContent FromFile(IFileSystem fs, FPath path, Hash128? hash, Action<Hash128>? hashed) => new LazyFileContent(fs, path, IsTextPath(path), hash, hashed);

    /// <summary>
    /// Creates content whose hash and length are known and whose bytes <paramref name="load"/> reads on first access:
    /// a blob of the disk cache.
    /// </summary>
    public static FileContent FromLoader(Hash128 hash, long length, bool isText, Func<CancellationToken, ValueTask<byte[]>> load) => new LoadedContent(hash, length, isText, load);

    /// <summary>
    /// The content of a file written to <paramref name="fs"/> at <paramref name="path"/> from <paramref name="written"/>,
    /// whose bytes are not kept: the file is read again on each access. A file passed on by <c>write</c> does not hold
    /// its content until the steps after it are done.
    /// </summary>
    public static FileContent FromWritten(IFileSystem fs, FPath path, long length, FileContent written) => new WrittenContent(fs, path, length, written.IsText, written.Hash);

    /// <summary>
    /// Whether the content is read from <paramref name="path"/> of <paramref name="fs"/> (<see cref="FromWritten"/>):
    /// writing it there again would truncate what it reads.
    /// </summary>
    public virtual bool IsReadFrom(IFileSystem fs, FPath path) => false;

    /// <summary>
    /// Whether a file is treated as text, based on its extension.
    /// </summary>
    public static bool IsTextPath(FPath path) => TextExtensions.Contains(path.GetExtensionWithDot());

    public abstract bool IsText { get; }

    /// <summary>
    /// Keeps the content in less memory while it waits for a step: a text drops its UTF-16 string for its UTF-8 bytes,
    /// in blocks small enough to stay out of the large object heap (<see cref="Utf8Blocks"/>). Reading it decodes it
    /// again, so the scheduler compacts only what waits (in front of a barrier, in the input of a reduce step).
    /// </summary>
    public virtual void Compact()
    {
    }

    public abstract long? Length { get; }

    /// <summary>
    /// The hash of the content bytes (UTF-8 for text). Computed once, on first access. For a file that is not loaded
    /// yet this blocks on the read: the scheduler calls <see cref="LoadAsync"/> on every consumed file before the step
    /// sees it, so steps never block here.
    /// </summary>
    public Hash128 Hash
    {
        get
        {
            if (!_hashed)
            {
                lock (_hashLock)
                {
                    if (!_hashed)
                    {
                        _hash = ComputeHash();
                        _hashed = true;
                    }
                }
            }

            return _hash;
        }
    }

    /// <summary>
    /// Whether <see cref="Hash"/> is known without reading anything.
    /// </summary>
    public bool IsHashKnown => _hashed;

    /// <summary>
    /// Loads the content when its hash is not known yet, so that <see cref="Hash"/> never blocks on a read.
    /// </summary>
    public ValueTask EnsureHashAsync(CancellationToken ct) => _hashed ? default : LoadAsync(ct);

    /// <summary>
    /// Reads and retains the bytes of a lazily read file, so that <see cref="Hash"/> and the readers then complete at
    /// once. Content that is already in memory completes synchronously.
    /// </summary>
    public virtual ValueTask LoadAsync(CancellationToken ct) => default;

    public abstract ValueTask<string> ReadTextAsync(CancellationToken ct);

    public abstract ValueTask<ReadOnlyMemory<byte>> ReadBytesAsync(CancellationToken ct);

    public abstract ValueTask<Stream> OpenReadAsync(CancellationToken ct);

    /// <summary>
    /// The bytes of the content, to store or write them once: unlike <see cref="ReadBytesAsync"/>, a text is encoded into a
    /// pooled buffer that the caller returns by disposing the result, and its encoding is not kept.
    /// </summary>
    public virtual async ValueTask<PooledBytes> RentBytesAsync(CancellationToken ct) => new(await ReadBytesAsync(ct), null);

    /// <summary>
    /// Writes the content to <paramref name="destination"/>, without keeping the encoding of a text.
    /// </summary>
    public async ValueTask CopyToAsync(Stream destination, CancellationToken ct)
    {
        using var bytes = await RentBytesAsync(ct);
        await destination.WriteAsync(bytes.Memory, ct);
    }

    protected abstract Hash128 ComputeHash();

    /// <summary>
    /// Decodes UTF-8, without the byte order mark.
    /// </summary>
    protected static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        var preamble = Encoding.UTF8.Preamble;

        if (bytes.StartsWith(preamble))
        {
            bytes = bytes[preamble.Length..];
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private sealed class TextContent : FileContent
    {
        // The string, until the content is compacted: then its UTF-8 blocks. A reference, replaced atomically.
        private volatile object _state;

        // The UTF-8 encoding of the string, made once when asked for. Published atomically, like the state.
        private volatile byte[]? _bytes;

        private long _length = -1;

        public TextContent(string text)
        {
            ArgumentNullException.ThrowIfNull(text);

            // The text is exposed without a byte order mark, whatever its origin.
            _state = text.Length > 0 && text[0] == '\ufeff' ? text[1..] : text;
        }

        public override bool IsText => true;

        public override long? Length
        {
            get
            {
                if (_length < 0)
                {
                    _length = _state is string text ? Encoding.UTF8.GetByteCount(text) : ((Utf8Blocks)_state).Length;
                }

                return _length;
            }
        }

        public override void Compact()
        {
            lock (_hashLock)
            {
                if (_state is not string text)
                {
                    return;
                }

                // Known before the string goes.
                _length = Encoding.UTF8.GetByteCount(text);

                if (!_hashed)
                {
                    _hash = Hash128.OfText(text);
                    _hashed = true;
                }

                _state = Utf8Blocks.Encode(text);
                _bytes = null;
            }
        }

        public override ValueTask<string> ReadTextAsync(CancellationToken ct)
        {
            return new(_state as string ?? ((Utf8Blocks)_state).Decode());
        }

        public override ValueTask<ReadOnlyMemory<byte>> ReadBytesAsync(CancellationToken ct)
        {
            // Benign race: two threads may encode the same text.
            return new(_state is string text ? _bytes ??= Encoding.UTF8.GetBytes(text) : ((Utf8Blocks)_state).ToArray());
        }

        public override async ValueTask<Stream> OpenReadAsync(CancellationToken ct)
        {
            var bytes = await ReadBytesAsync(ct);
            return new ReadOnlyMemoryStream(bytes);
        }

        public override ValueTask<PooledBytes> RentBytesAsync(CancellationToken ct)
        {
            // Read once: a compaction may replace it.
            var state = _state;

            if (state is Utf8Blocks blocks)
            {
                var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(blocks.Length);
                blocks.CopyTo(rented);
                return new(new PooledBytes(rented.AsMemory(0, blocks.Length), rented));
            }

            if (_bytes is { } bytes)
            {
                return new(new PooledBytes(bytes, null));
            }

            var text = (string)state;
            var array = System.Buffers.ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(text));
            var length = Encoding.UTF8.GetBytes(text, array);
            return new(new PooledBytes(array.AsMemory(0, length), array));
        }

        // A compacted text has its hash.
        protected override Hash128 ComputeHash() => Hash128.OfText((string)_state);
    }

    private sealed class BytesContent : FileContent
    {
        private readonly ReadOnlyMemory<byte> _bytes;
        private string? _text;

        public BytesContent(ReadOnlyMemory<byte> bytes, bool isText)
        {
            _bytes = bytes;
            IsText = isText;
        }

        public override bool IsText { get; }

        public override long? Length => _bytes.Length;

        public override ValueTask<string> ReadTextAsync(CancellationToken ct)
        {
            _text ??= DecodeText(_bytes.Span);
            return new(_text);
        }

        public override ValueTask<ReadOnlyMemory<byte>> ReadBytesAsync(CancellationToken ct) => new(_bytes);

        public override ValueTask<Stream> OpenReadAsync(CancellationToken ct) => new(new ReadOnlyMemoryStream(_bytes));

        protected override Hash128 ComputeHash() => Hash128.Of(_bytes.Span);
    }

    private sealed class LazyFileContent : FileContent
    {
        private readonly IFileSystem _fileSystem;
        private readonly FPath _path;
        private readonly Lazy<Task<byte[]>> _bytes;
        private readonly Action<Hash128>? _onHashed;
        private string? _text;

        public LazyFileContent(IFileSystem fileSystem, FPath path, bool isText, Hash128? hash, Action<Hash128>? hashed)
            : base(hash)
        {
            _fileSystem = fileSystem;
            _path = path;
            _onHashed = hashed;
            IsText = isText;
            _bytes = new Lazy<Task<byte[]>>(LoadAsync, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public override bool IsText { get; }

        public override long? Length => _bytes.IsValueCreated && _bytes.Value.IsCompletedSuccessfully ? _bytes.Value.Result.Length : null;

        public override async ValueTask LoadAsync(CancellationToken ct)
        {
            await _bytes.Value.WaitAsync(ct);
        }

        public override async ValueTask<string> ReadTextAsync(CancellationToken ct)
        {
            if (_text != null)
            {
                return _text;
            }

            var bytes = await _bytes.Value.WaitAsync(ct);
            _text = DecodeText(bytes);
            return _text;
        }

        public override async ValueTask<ReadOnlyMemory<byte>> ReadBytesAsync(CancellationToken ct)
        {
            return await _bytes.Value.WaitAsync(ct);
        }

        public override async ValueTask<Stream> OpenReadAsync(CancellationToken ct)
        {
            if (!_bytes.IsValueCreated)
            {
                // Stream large files directly instead of loading them.
                return await _fileSystem.OpenFileAsync(_path);
            }

            return new ReadOnlyMemoryStream(await _bytes.Value.WaitAsync(ct));
        }

        protected override Hash128 ComputeHash()
        {
            // Blocks only when LoadAsync was not awaited first, which the scheduler always does for consumed files.
            var bytes = _bytes.Value.GetAwaiter().GetResult();
            var hash = Hash128.Of(bytes);
            _onHashed?.Invoke(hash);
            return hash;
        }

        private async Task<byte[]> LoadAsync()
        {
            return await _fileSystem.ReadFileAsync(_path);
        }
    }

    /// <summary>
    /// A file of the output folder as <c>write</c> wrote it: its hash and length are known, its bytes are read on each
    /// access and not kept.
    /// </summary>
    private sealed class WrittenContent(IFileSystem fileSystem, FPath path, long length, bool isText, Hash128 hash) : FileContent(hash)
    {
        public override bool IsText { get; } = isText;

        public override long? Length => length;

        public override bool IsReadFrom(IFileSystem fs, FPath path1) => ReferenceEquals(fs, fileSystem) && path1 == path;

        public override async ValueTask<string> ReadTextAsync(CancellationToken ct) => DecodeText((await ReadBytesAsync(ct)).Span);

        public override async ValueTask<ReadOnlyMemory<byte>> ReadBytesAsync(CancellationToken ct)
        {
            await using var stream = await fileSystem.OpenFileAsync(path);
            var bytes = new byte[length];
            await stream.ReadExactlyAsync(bytes, ct);
            return bytes;
        }

        public override async ValueTask<Stream> OpenReadAsync(CancellationToken ct) => await fileSystem.OpenFileAsync(path);

        // The hash is known: it is never computed.
        protected override Hash128 ComputeHash() => throw new InvalidOperationException("The hash of written content is known");
    }

    /// <summary>
    /// Content read on first access by a loader, whose hash and length are known (a blob of the disk cache).
    /// </summary>
    private sealed class LoadedContent : FileContent
    {
        private readonly long _length;
        private readonly Func<CancellationToken, ValueTask<byte[]>> _load;
        private readonly Lock _lock = new();
        private Task<byte[]>? _bytes;
        private string? _text;

        public LoadedContent(Hash128 hash, long length, bool isText, Func<CancellationToken, ValueTask<byte[]>> load) : base(hash)
        {
            _length = length;
            _load = load;
            IsText = isText;
        }

        public override bool IsText { get; }

        public override long? Length => _length;

        public override async ValueTask LoadAsync(CancellationToken ct) => await GetBytesAsync().WaitAsync(ct);

        public override async ValueTask<string> ReadTextAsync(CancellationToken ct)
        {
            if (_text != null)
            {
                return _text;
            }

            var bytes = await GetBytesAsync().WaitAsync(ct);
            _text = DecodeText(bytes);
            return _text;
        }

        public override async ValueTask<ReadOnlyMemory<byte>> ReadBytesAsync(CancellationToken ct) => await GetBytesAsync().WaitAsync(ct);

        public override async ValueTask<Stream> OpenReadAsync(CancellationToken ct) => new ReadOnlyMemoryStream(await GetBytesAsync().WaitAsync(ct));

        // The hash is known: it is never computed.
        protected override Hash128 ComputeHash() => throw new InvalidOperationException("The hash of cached content is known");

        private Task<byte[]> GetBytesAsync()
        {
            if (_bytes != null)
            {
                return _bytes;
            }

            lock (_lock)
            {
                return _bytes ??= _load(CancellationToken.None).AsTask();
            }
        }
    }

    /// <summary>
    /// UTF-8 bytes in blocks of <see cref="BlockSize"/> bytes at most, below the size of the large object heap: a large
    /// heap is collected only with the whole heap and is not compacted, so large contents that come and go fragment it.
    /// </summary>
    internal sealed class Utf8Blocks
    {
        public const int BlockSize = 64 * 1024;

        private readonly byte[][] _blocks;

        private Utf8Blocks(byte[][] blocks, int length)
        {
            _blocks = blocks;
            Length = length;
        }

        public int Length { get; }

        public static Utf8Blocks Encode(string text)
        {
            var encoder = Encoding.UTF8.GetEncoder();
            var blocks = new List<byte[]>();
            var chars = text.AsSpan();
            var length = 0;
            var buffer = new byte[BlockSize];

            while (true)
            {
                encoder.Convert(chars, buffer, flush: true, out var used, out var written, out var completed);
                chars = chars[used..];

                if (written > 0)
                {
                    blocks.Add(written == buffer.Length ? buffer : buffer[..written]);
                    length += written;
                    buffer = new byte[BlockSize];
                }

                if (completed)
                {
                    return new Utf8Blocks([.. blocks], length);
                }
            }
        }

        public string Decode()
        {
            var decoder = Encoding.UTF8.GetDecoder();
            var chars = 0;

            foreach (var block in _blocks)
            {
                chars += decoder.GetCharCount(block, flush: false);
            }

            chars += decoder.GetCharCount([], flush: true);
            decoder.Reset();

            return string.Create(chars, this, static (span, self) =>
            {
                var decoder = Encoding.UTF8.GetDecoder();

                for (var i = 0; i < self._blocks.Length; i++)
                {
                    var written = decoder.GetChars(self._blocks[i], span, flush: i == self._blocks.Length - 1);
                    span = span[written..];
                }
            });
        }

        public void CopyTo(Span<byte> destination)
        {
            foreach (var block in _blocks)
            {
                block.CopyTo(destination);
                destination = destination[block.Length..];
            }
        }

        public byte[] ToArray()
        {
            var bytes = new byte[Length];
            CopyTo(bytes);
            return bytes;
        }
    }

    /// <summary>
    /// A read-only stream over shared bytes, so content is never copied to be read.
    /// </summary>
    private sealed class ReadOnlyMemoryStream : Stream
    {
        private readonly ReadOnlyMemory<byte> _memory;
        private int _position;

        public ReadOnlyMemoryStream(ReadOnlyMemory<byte> memory)
        {
            _memory = memory;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _memory.Length;

        public override long Position
        {
            get => _position;
            set => _position = (int)Math.Clamp(value, 0, _memory.Length);
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var remaining = _memory.Length - _position;
            var count = Math.Min(remaining, buffer.Length);
            _memory.Span.Slice(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromResult(Read(buffer, offset, count));

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => _memory.Length + offset,
            };

            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// Bytes that may come from <see cref="System.Buffers.ArrayPool{T}.Shared"/>, returned to it when disposed.
/// </summary>
internal readonly struct PooledBytes(ReadOnlyMemory<byte> memory, byte[]? rented) : IDisposable
{
    public ReadOnlyMemory<byte> Memory { get; } = memory;

    public void Dispose()
    {
        if (rented != null)
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
