namespace Douze.FileSystems
{
    internal class MemoryFileStream : Stream
    {
        private readonly MemoryStream _stream = new MemoryStream();

        public FileMetadata Metadata { get; }

        public override bool CanRead => _stream.CanRead;

        public override bool CanSeek => _stream.CanSeek;

        public override bool CanWrite => _stream.CanWrite;

        public override long Length => _stream.Length;

        public override long Position { get => _stream.Position; set => _stream.Position = value; }

        public MemoryFileStream()
        {
            Metadata = new FileMetadata(0, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow);
        }

        public override void Flush()
        {
            _stream.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Metadata.LastAccessTimeUtc = DateTime.UtcNow;

            return _stream.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return _stream.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            Metadata.Length = value;

            _stream.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Metadata.LastWriteTimeUtc = DateTime.UtcNow;

            _stream.Write(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            Metadata.Length = _stream.Length;
            _stream.Seek(0, SeekOrigin.Begin);
        }
    }
}
