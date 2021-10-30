using System.Collections.Concurrent;
using Douze.FileSystems;
using Douze.Results;

namespace Douze.FileProcessors;

internal class FrontMatterProcessor : IFileProcessor
{
    public Predicate<FPath> Match { get; set; } = _ => true;

    public FrontMatterProcessor(Predicate<FPath> match)
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

            if (document is SourceDocument sourceDocument)
            {
                using var stream = await document.OpenAsync();

                var filecontent = await FrontMatterHelper.MergeFrontMatterData(options, document.Data, stream);

                if (filecontent != null)
                {
                    result.Add(new DocumentResult(new TextDocument(options.OutputFileSystem, document.Path, filecontent, document.Data, document.ContentType)));
                }
                else
                {
                    result.Add(new DocumentResult(document));
                }
            }
        });

        return result;
    }
}
