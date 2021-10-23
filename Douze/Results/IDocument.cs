using System.Text.Json.Nodes;
using Douze.FileSystems;

namespace Douze.Results;

internal interface IDocument
{
    JsonObject Data { get; }

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
