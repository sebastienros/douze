using System.Collections.Concurrent;
using Douze.FileSystems;
using Douze.Results;

namespace Douze.FileProcessors;

internal sealed class CopyDocumentProcessor : IFileProcessor
{
    public Predicate<FPath> Match { get; set; } = _ => true;

    public CopyDocumentProcessor(Predicate<FPath> match)
    {
        Match = match;
    }

    public async Task<IEnumerable<IResult>> ProcessFileAsync(IEnumerable<IDocument> documents, Options options)
    {
        var result = new ConcurrentBag<IResult>();

        await Parallel.ForEachAsync(documents, new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, (document, cancellationToken) =>
        {

            if (!Match(document.Path))
            {
                result.Add(new DocumentsResult(document));
                return ValueTask.CompletedTask;
            }

            result.Add(new DocumentsResult($"Copying file {document.Path}", new CopyDocument(document, options.OutputFileSystem)));

            return ValueTask.CompletedTask;
        });

        return result;
    }
}
