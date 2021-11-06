using System.Collections.Concurrent;
using Douze.FileSystems;
using Douze.Nodes;
using Douze.Results;

namespace Douze.FileProcessors;

internal class PageMetadataProcessor : IFileProcessor
{
    public Predicate<FPath> Match { get; set; } = _ => true;

    public async Task<IEnumerable<IResult>> ProcessFileAsync(IEnumerable<IDocument> documents, Options options)
    {
        var result = new ConcurrentBag<IResult>();

        await Parallel.ForEachAsync(documents, new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, async (document, cancellationToken) =>
        {

            // Pass-through
            result.Add(new DocumentResult(document));

            if (Match(document.Path) && document is SourceDocument sourceDocument)
            {
                var metadata = await sourceDocument.FileSystem.GetFileMetadaAsync(document.Path);

                var page = document.Data["page"] as NodeObject;

                if (page == null)
                {
                    document.Data["page"] = page = new NodeObject();
                }

                page.Add("date", metadata.LastWriteTimeUtc);
            }
        });

        return result;
    }
}
