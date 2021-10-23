using System.Text.Json.Nodes;
using Douze.FileSystems;

namespace Douze.Results;

internal class SourceDocument : IDocument
{
    public SourceDocument(IFileSystem fileSystem, FPath path)
    {
        Path = path;
        FileSystem = fileSystem;
    }

    public JsonObject Data { get; set; } = new JsonObject();

    public FPath Path { get; set; }

    public IFileSystem FileSystem { get; }

    public string? ContentType { get; set; }

    public ValueTask<Stream> OpenAsync()
    {
        return FileSystem.OpenFileAsync(Path);
    }

    public Task SaveFileAsync()
    {
        return Task.CompletedTask;
    }
}
