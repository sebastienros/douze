using Douze.FileSystems;
using Douze.Nodes;

namespace Douze.Results;

internal interface IDocument
{
    NodeObject Data { get; }

    /// <summary>
    /// Gets whether the document is a content document.
    /// </summary>
    public bool IsContent { get; }

    /// <summary>
    /// Gets the relative path of the file.
    /// </summary>
    FPath Path { get; }

    /// <summary>
    /// Gets the stream of the file.
    /// </summary>
    ValueTask<Stream> OpenAsync();

    /// <summary>
    /// Gets the content-type of the file.
    /// </summary>
    string? ContentType { get; }

    /// <summary>
    /// Saves the file.
    /// </summary>
    Task SaveFileAsync();
}
