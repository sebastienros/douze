using Douze.FileSystems;
using Douze.Results;
using System.Text.Json.Nodes;

namespace Douze.FileProcessors;

internal interface IFileProcessor
{
    IAsyncEnumerable<IResult> ProcessFileAsync(FileEntry file, Options options, JsonObject data);
}
