using System.Collections.Concurrent;
using Douze.FileSystems;
using Douze.Results;

namespace Douze.FileProcessors;

/// <summary>
/// Assigns the FrontMatter data of a document to its Data property, and removes the header.
/// </summary>
internal class FrontMatterProcessor : IFileProcessor
{
    public Predicate<FPath> Match { get; set; } = _ => true;

    public FrontMatterProcessor(Predicate<FPath> match)
    {
        Match = match;
    }

    public async Task<IEnumerable<IResult>> ProcessFileAsync(IEnumerable<IDocument> documents, Options options)
    {
        var results = new ConcurrentBag<IResult>();
        var matches = new List<IDocument>();

        foreach (var document in documents)
        {
            if (Match == null || Match(document.Path))
            {
                matches.Add(document);
            }
            else
            {
                results.Add(new DocumentsResult(document));
            }
        }

        await Parallel.ForEachAsync(matches, new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, async (document, cancellationToken) =>
        {
            using var stream = await document.OpenAsync();

            var filecontent = await FrontMatterHelper.MergeFrontMatterData(options, document.Data, stream);

            if (filecontent != null)
            {
                results.Add(new DocumentsResult(new TextDocument(options.OutputFileSystem, document.Path, filecontent, document.Data, document.ContentType, DocumentTypes.Content)));
            }
            else
            {
                results.Add(new DocumentsResult(document));
            }
        });

        return results;
    }
}
