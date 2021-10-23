using System.Text;
using System.Text.Json.Nodes;
using Douze.FileSystems;

namespace Douze.Results;

internal sealed class TextDocument : IDocument
{
    public TextDocument(IFileSystem fileSystem, FPath filePath, string content, JsonObject data, string? contentType)
    {
        FileSystem = fileSystem;
        Path = filePath;
        Content = content;
        ContentType = contentType;
        Data = data;
    }
    public JsonObject Data { get; set; }
    public IFileSystem FileSystem { get; }
    public FPath Path { get; }
    public string Content { get; }
    public string? ContentType { get; }

    public ValueTask<Stream> OpenAsync()
    {
        return new (new MemoryStream(Encoding.UTF8.GetBytes(Content)));
    }

    public async Task SaveFileAsync()
    {
        using var stream = await FileSystem.CreateFileAsync(Path);
        using var sw = new StreamWriter(stream);
        await sw.WriteAsync(Content);
    }
}
