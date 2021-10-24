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

    public async IAsyncEnumerable<IResult> ProcessFileAsync(IDocument document, Options options)
    {
        if (!Match(document.Path))
        {
            yield return new DocumentResult(document);
            yield break;
        }

        yield return new DocumentResult($"Copying file {document.Path}", new CopyDocument(document, options.OutputFileSystem));

        await Task.Yield();
    }
}
