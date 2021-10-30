using System.Text.Encodings.Web;
using Douze.FileSystems;
using Douze.Results;
using Fluid;
using Fluid.Values;

namespace Douze.FileProcessors;

internal class LayoutFileProcessor : IFileProcessor
{
    public Predicate<FPath> Match { get; set; } = _ => true;

    public LayoutFileProcessor(Predicate<FPath> match)
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

        using var stream = await document.OpenAsync();
        using var sr = new StreamReader(stream);

        var content = await sr.ReadToEndAsync();

        // Apply layout

        var html = await LayoutHelper.ProcessLayoutAsync(options, content, document.Data, new Dictionary<string, FluidValue>());

        yield return new DocumentResult($"Applying layout for {document.Path}", new TextDocument(options.OutputFileSystem, document.Path, html, document.Data, document.ContentType));
    }
}
