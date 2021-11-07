using System.Text.Json.Nodes;
using Douze.Results;

namespace Douze.FileProcessors;

/// <summary>
/// Assigns the JSON data of a document to its Data property.
/// </summary>
internal sealed class JsonDataFileProcessor : IFileProcessor
{
    public async Task<IEnumerable<IResult>> ProcessFileAsync(IEnumerable<IDocument> documents, Options options)
    {
        var result = new List<IResult>();
        var fitleredDocuments = new List<IDocument>();

        foreach (var document in documents)
        {
            if (document.Path.GetExtensionWithDot().ToLowerInvariant() != ".json")
            {
                result.Add(new DocumentsResult(document));
            }
            else
            {
                document.DocumentType = DocumentTypes.Data;
                result.Add(new DocumentsResult($"Reading json data file {document.Path}", document));
                fitleredDocuments.Add(document);
            }
        }

        await Parallel.ForEachAsync(fitleredDocuments, new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, async (document, cancellationToken) =>
        {
            using var stream = await document.OpenAsync();

            // Parse the json content with System.Text.Json
            var json = JsonNode.Parse(stream)?.AsObject();

            if (json != null)
            {
                var o = JsonHelper.Convert(json)?.AsObject();

                if (o != null)
                {
                    document.Data.Merge(o);
                }
            }
        });

        return result;
    }
}
