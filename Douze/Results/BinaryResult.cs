namespace Douze.Results
{
    internal class BinaryResult : IResult
    {
        public BinaryResult(string filePath, string description, byte[] content, string contentType)
        {
            FilePath = filePath;
            Description = description;
            Content = content;
            ContentType = contentType;
        }

        public string FilePath { get; }
        public string Description { get; }
        public byte[] Content { get; }
        public string ContentType { get; }

        public async Task SaveFileAsync(Stream outputStream)
        {
            using (var ms = new MemoryStream(Content))
            {
                await ms.CopyToAsync(outputStream);
            }
        }
    }
}
