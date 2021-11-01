using Douze.FileSystems;
using Douze.Nodes;

namespace Douze.Results;

internal class SourceDocument : IDocument
{
    public SourceDocument(IFileSystem fileSystem, FPath path)
    {
        Path = path;
        FileSystem = fileSystem;
    }

    public bool IsContent => false;
    public NodeObject Data { get; set; } = new NodeObject();

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
