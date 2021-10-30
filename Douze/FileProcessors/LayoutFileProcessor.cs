using System.Collections.Concurrent;
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

            using var stream = await document.OpenAsync();
            using var sr = new StreamReader(stream);

            var content = await sr.ReadToEndAsync();

            // Apply layout

            var html = await LayoutHelper.ProcessLayoutAsync(options, content, document.Data);

            result.Add(new DocumentResult($"Applying layout for {document.Path}", new TextDocument(options.OutputFileSystem, document.Path, html, document.Data, document.ContentType)));
        });

        return result;
    }
}
