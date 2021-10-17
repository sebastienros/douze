using Zio;

namespace Douze.Results;

internal sealed class BinaryResult : IResult
{
    public BinaryResult(UPath filePath, string description, byte[] content, string contentType)
    {
        FilePath = filePath;
        Description = description;
        Content = content;
        ContentType = contentType;
    }

    public UPath FilePath { get; }
    public string Description { get; }
    public byte[] Content { get; }
    public string ContentType { get; }

    public async Task SaveFileAsync(Stream outputStream)
    {
        await using var ms = new MemoryStream(Content);
        await ms.CopyToAsync(outputStream);
    }
}
