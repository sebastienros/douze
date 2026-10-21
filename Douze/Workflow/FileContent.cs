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

    private readonly Lazy<Hash128> _hash;

    protected FileContent()
    {
        _hash = new Lazy<Hash128>(ComputeHash, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static readonly FileContent Empty = new TextContent("");

    public static FileContent FromText(string text) => new TextContent(text);

    public static FileContent FromBytes(ReadOnlyMemory<byte> bytes, bool isText) => new BytesContent(bytes, isText);

    /// <summary>
    /// Creates content that reads the file once, on first access, and then retains its bytes.
    /// </summary>
    public static FileContent FromFile(IFileSystem fs, FPath path, long length) => new LazyFileContent(fs, path, length, IsTextPath(path));

    /// <summary>
    /// Whether a file is treated as text, based on its extension.
    /// </summary>
    public static bool IsTextPath(FPath path) => TextExtensions.Contains(path.GetExtensionWithDot());

    public abstract bool IsText { get; }

    public abstract long? Length { get; }

    /// <summary>
    /// The hash of the content bytes (UTF-8 for text). Computed once, on first access. For a file that is not loaded
    /// yet this blocks on the read: the scheduler calls <see cref="LoadAsync"/> on every consumed file before the step
    /// sees it, so steps never block here.
    /// </summary>
    public Hash128 Hash => _hash.Value;

    /// <summary>
    /// Reads and retains the bytes of a lazily read file, so that <see cref="Hash"/> and the readers then complete at
    /// once. Content that is already in memory completes synchronously.
    /// </summary>
    public virtual ValueTask LoadAsync(CancellationToken ct) => default;

    public abstract ValueTask<string> ReadTextAsync(CancellationToken ct);

    public abstract ValueTask<ReadOnlyMemory<byte>> ReadBytesAsync(CancellationToken ct);

    public abstract ValueTask<Stream> OpenReadAsync(CancellationToken ct);

    public async ValueTask CopyToAsync(Stream destination, CancellationToken ct)
    {
        await using var stream = await OpenReadAsync(ct);
        await stream.CopyToAsync(destination, ct);
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
        private readonly string _text;
        private ReadOnlyMemory<byte>? _bytes;

        public TextContent(string text)
        {
            ArgumentNullException.ThrowIfNull(text);

            // The text is exposed without a byte order mark, whatever its origin.
            _text = text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
        }

        public override bool IsText => true;

        public override long? Length => Encoding.UTF8.GetByteCount(_text);

        public override ValueTask<string> ReadTextAsync(CancellationToken ct) => new(_text);

        public override ValueTask<ReadOnlyMemory<byte>> ReadBytesAsync(CancellationToken ct)
        {
            // Benign race: two threads may encode the same text.
            _bytes ??= Encoding.UTF8.GetBytes(_text);
            return new(_bytes.Value);
        }

        public override async ValueTask<Stream> OpenReadAsync(CancellationToken ct)
        {
            var bytes = await ReadBytesAsync(ct);
            return new ReadOnlyMemoryStream(bytes);
        }

        protected override Hash128 ComputeHash() => Hash128.OfText(_text);
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
        private readonly long _lengthHint;
        private readonly Lazy<Task<byte[]>> _bytes;
        private string? _text;

        public LazyFileContent(IFileSystem fileSystem, FPath path, long lengthHint, bool isText)
        {
            _fileSystem = fileSystem;
            _path = path;
            _lengthHint = lengthHint;
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
            return Hash128.Of(bytes);
        }

        private async Task<byte[]> LoadAsync()
        {
            await using var stream = await _fileSystem.OpenFileAsync(_path);
            using var memory = new MemoryStream(_lengthHint > 0 && _lengthHint < int.MaxValue ? (int)_lengthHint : 0);
            await stream.CopyToAsync(memory);
            return memory.ToArray();
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
