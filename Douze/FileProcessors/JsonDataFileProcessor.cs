using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Douze.Results;

namespace Douze.FileProcessors;

internal sealed class JsonDataFileProcessor : IFileProcessor
{
    public async Task<IEnumerable<IResult>> ProcessFileAsync(IEnumerable<IDocument> documents, Options options)
    {
        var result = new ConcurrentBag<IResult>();

        await Parallel.ForEachAsync(documents, new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, async (document, cancellationToken) =>
        {

            if (document.Path.GetExtensionWithDot().ToLowerInvariant() != ".json")
            {
                result.Add(new DocumentResult(document));
                return;
            }

            using var stream = await document.OpenAsync();

            var json = JsonNode.Parse(stream)?.AsObject();

            if (json != null)
            {
                JsonHelper.Merge(document.Data, json);
            }

            result.Add(new DocumentResult($"Reading json data file {document.Path}"));
        });

        return result;
    }
}
