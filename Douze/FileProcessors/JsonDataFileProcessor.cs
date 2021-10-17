using Douze.Results;
using System.Text.Json.Nodes;
using Zio;

namespace Douze.FileProcessors;

internal sealed class JsonDataFileProcessor : IFileProcessor
{
    public async IAsyncEnumerable<IResult> ProcessFileAsync(FileEntry file, Options options, JsonObject data)
    {

        using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var document = JsonNode.Parse(stream)?.AsObject();

        if (document != null)
        {
            JsonHelper.Merge(data, document);
        }

        await Task.Yield();

        yield return new NullResult($"Reading json data file {file.Path}");
    }
}
