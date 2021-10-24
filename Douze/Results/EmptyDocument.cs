using System.Text.Json.Nodes;
using Douze.FileSystems;

namespace Douze.Results;

internal sealed class EmptyDocument : IDocument
{
    public EmptyDocument()
    {
        Data = new JsonObject();
    }

    public FPath Path => FPath.Empty;
    public string? ContentType => null;

    public JsonObject Data { get; set; }

    public ValueTask<Stream> OpenAsync()
    {
        return new ValueTask<Stream>(new MemoryStream());
    }

    public Task SaveFileAsync()
    {
        return Task.CompletedTask;
    }
}
