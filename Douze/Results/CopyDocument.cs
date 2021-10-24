using System.Text.Json.Nodes;
using Douze.FileSystems;

namespace Douze.Results;

internal class CopyDocument : IDocument
{
    public CopyDocument(IDocument document, IFileSystem newFileSystem)
    {
        FileSystem = newFileSystem;
        Document = document;
        Path = document.Path;
        ContentType = document.ContentType;
        Data = document.Data;
    }

    public JsonObject Data { get; set; }

    public FPath Path { get; set; }
    public IDocument Document { get; }
    public IFileSystem FileSystem { get; }

    public string? ContentType { get; set; }

    public ValueTask<Stream> OpenAsync()
    {
        return Document.OpenAsync();
    }

    public async Task SaveFileAsync()
    {
        using var source = await OpenAsync();
        using   var stream = await FileSystem.CreateFileAsync(Path);
        await source.CopyToAsync(stream);
    }
}
