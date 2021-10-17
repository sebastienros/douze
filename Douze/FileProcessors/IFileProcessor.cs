using Douze.Results;
using System.Text.Json.Nodes;
using Zio;

namespace Douze.FileProcessors;

internal interface IFileProcessor
{
    IAsyncEnumerable<IResult> ProcessFileAsync(FileEntry file, Options options, JsonObject data);
}
