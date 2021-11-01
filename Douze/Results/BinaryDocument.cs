using Douze.FileSystems;
using Douze.Nodes;

namespace Douze.Results;

internal sealed class BinaryDocument : IDocument
{
    public BinaryDocument(IFileSystem fileSystem, FPath filePath, byte[] content, string contentType)
    {
        FileSystem = fileSystem;
        Path = filePath;
        Content = content;
        ContentType = contentType;
        Data = new NodeObject();
    }
    public bool IsContent => false;
    public NodeObject Data { get; set; }
    public IFileSystem FileSystem { get; }
    public FPath Path { get; }
    public byte[] Content { get; }
    public string ContentType { get; }

    public ValueTask<Stream> OpenAsync()
    {
        return new(new MemoryStream(Content));
    }

    public async Task SaveFileAsync()
    {
        var stream = await FileSystem.CreateFileAsync(Path);
        await using var ms = new MemoryStream(Content);
        await ms.CopyToAsync(stream);
    }
}
