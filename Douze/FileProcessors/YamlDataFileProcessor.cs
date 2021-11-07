using Douze.Results;

namespace Douze.FileProcessors;

/// <summary>
/// Assigns the YAML data of a document to its Data property.
/// </summary>
internal sealed class YamlDataFileProcessor : IFileProcessor
{
    public async Task<IEnumerable<IResult>> ProcessFileAsync(IEnumerable<IDocument> documents, Options options)
    {
        var result = new List<IResult>();
        var fitleredDocuments = new List<IDocument>();

        foreach (var document in documents)
        {
            if (document.Path.GetExtensionWithDot().ToLowerInvariant() != ".yml")
            {
                result.Add(new DocumentsResult(document));
            }
            else
            {
                document.DocumentType = DocumentTypes.Data;
                result.Add(new DocumentsResult($"Reading yaml data file {document.Path}", document));
                fitleredDocuments.Add(document);
            }
        }

        await Parallel.ForEachAsync(fitleredDocuments, new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, async (document, cancellationToken) =>
        {
            using var stream = await document.OpenAsync();
            using var reader = new StreamReader(stream);

            var yaml = YamlHelper.Parse(reader);

            document.Data.Merge(yaml?.AsObject());
        });

        return result;
    }
}
