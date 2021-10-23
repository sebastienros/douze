using System.Text.Json.Nodes;
using Douze.Results;

namespace Douze.FileProcessors;

internal sealed class JsonDataFileProcessor : IFileProcessor
{
    public async IAsyncEnumerable<IResult> ProcessFileAsync(IDocument document, Options options)
    {
        if (document.Path.GetExtensionWithDot().ToLowerInvariant() != ".json")
        {
            yield return new DocumentResult(document);
            yield break;
        }

        using var stream = await document.OpenAsync();

        var json = JsonNode.Parse(stream)?.AsObject();

        if (json != null)
        {
            JsonHelper.Merge(document.Data, json);
        }

        yield return new DocumentResult($"Reading json data file {document.Path}");
    }
}
