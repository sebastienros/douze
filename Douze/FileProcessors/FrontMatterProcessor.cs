using System.Text.Json.Nodes;
using Douze.FileSystems;
using Douze.Results;

namespace Douze.FileProcessors;

/// <summary>
/// Implements
/// - FrontMatter
/// - Liquid pre-processing
/// - Layout chaining
/// - Permalink
/// </summary>
internal class FrontMatterProcessor : IFileProcessor
{
    public Predicate<FPath>? Match { get; set; }

    public FrontMatterProcessor(Predicate<FPath> match)
    {
        Match = match;
    }

    public async IAsyncEnumerable<IResult> ProcessFileAsync(IDocument document, Options options)
    {
        if (Match != null && !Match(document.Path))
        {
            yield return new DocumentResult(document);
            yield break;
        }

        if (document is SourceDocument sourceDocument)
        {
            using var stream = await document.OpenAsync();

            var filecontent = await FrontMatterHelper.MergeFrontMatterData(options, document.Data, stream);

            if (filecontent != null)
            {
                yield return new DocumentResult(new TextDocument(options.OutputFileSystem, document.Path, filecontent, document.Data, document.ContentType));
            }
            else
            {
                yield return new DocumentResult(document);
            }
        }
    }
}
