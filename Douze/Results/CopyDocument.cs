using Douze.FileSystems;
using Douze.Nodes;

namespace Douze.Results;

/// <summary>
/// Creates a copy of an existing document and an optional new file name.
/// </summary>
internal class CopyDocument : IDocument
{
    public CopyDocument(IDocument document, IFileSystem newFileSystem, FPath newPath)
    {
        FileSystem = newFileSystem;
        Document = document;
        Path = newPath;
        ContentType = document.ContentType;
        Data = document.Data;
    }

    public CopyDocument(IDocument document, IFileSystem newFileSystem)
    {
        FileSystem = newFileSystem;
        Document = document;
        Path = document.Path;
        ContentType = document.ContentType;
        Data = document.Data;
    }

    public bool IsContent => Document.IsContent;
    public NodeObject Data { get; set; }

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
        using var stream = await FileSystem.CreateFileAsync(Path);
        await source.CopyToAsync(stream);
    }
}
