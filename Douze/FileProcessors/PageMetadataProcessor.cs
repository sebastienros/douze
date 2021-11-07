using Douze.FileSystems;
using Douze.Nodes;
using Douze.Results;

namespace Douze.FileProcessors;

/// <summary>
/// Adds teh page data to each document
/// </summary>
internal class PageMetadataProcessor : IFileProcessor
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
            var metadata = await document.FileSystem.GetFileMetadaAsync(document.Path);

            var page = document.Data["page"] as NodeObject;

            if (page == null)
            {
                document.Data["page"] = page = new NodeObject();
            }

            page.Add("date", metadata.LastWriteTimeUtc);
        });

        return documents.Select(x => new DocumentsResult(x)).ToArray();
    }
}
