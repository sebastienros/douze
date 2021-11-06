using Douze.FileSystems;
using Douze.Nodes;

namespace Douze.Results;

internal sealed class EmptyDocument : IDocument
{
    public EmptyDocument()
    {
        Data = new NodeObject();
    }

    public bool IsContent => false;
    public FPath Path => FPath.Empty;
    public string? ContentType => null;

    public NodeObject Data { get; set; }

    public ValueTask<Stream> OpenAsync()
    {
        return new ValueTask<Stream>(new MemoryStream());
    }

    public Task SaveFileAsync()
    {
        return Task.CompletedTask;
    }
}
