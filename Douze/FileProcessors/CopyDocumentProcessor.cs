using System.Collections.Concurrent;
using Douze.FileSystems;
using Douze.Results;

namespace Douze.FileProcessors;

internal class CopyDocumentProcessor : IFileProcessor
{
    public Predicate<FPath> Match { get; set; } = _ => true;

    public CopyDocumentProcessor(Predicate<FPath> match)
    {
        Match = match;
    }

    public async Task<IEnumerable<IResult>> ProcessFileAsync(IEnumerable<IDocument> documents, Options options)
    {
        var result = new ConcurrentBag<IResult>();

        await Parallel.ForEachAsync(documents, new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, async (document, cancellationToken) =>
        {

            if (!Match(document.Path))
            {
                result.Add(new DocumentResult(document));
                return;
            }

            result.Add(new DocumentResult($"Copying file {document.Path}", new CopyDocument(document, options.OutputFileSystem)));

            await Task.Yield();
        });

        return result;
    }
}
