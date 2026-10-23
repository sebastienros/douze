using Douze.FileSystems;
using Douze.Nodes;
using Douze.Results;

namespace Douze.FileProcessors;

/// <summary>
/// Adds the page data to each document
/// </summary>
internal sealed class PageMetadataProcessor : IFileProcessor
{
    public Predicate<FPath>? Match { get; set; } = null;

    public async Task<IEnumerable<IResult>> ProcessFileAsync(IEnumerable<IDocument> documents, Options options)
    {
        var filteredDocuments = new List<SourceDocument>();

        foreach (var document in documents)
        {
            if ((Match == null || Match(document.Path)) && document is SourceDocument sourceDocument)
            {
                filteredDocuments.Add(sourceDocument);
            }
        }

        await Parallel.ForEachAsync(filteredDocuments, new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, static async (document, cancellationToken) =>
        {
            var metadata = await document.FileSystem.GetFileMetadataAsync(document.Path);

            if (document.Data["page"] is not NodeObject page)
            {
                document.Data["page"] = page = new NodeObject();
            }

            page.Add("date", metadata.LastWriteTimeUtc);
            page.Add("inputPath", document.Path.FullName);
        });

        return documents.Select(x => new DocumentsResult(x)).ToArray();
    }
}
