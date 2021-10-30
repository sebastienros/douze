using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using Douze.FileSystems;
using Douze.Results;
using Fluid;
using Fluid.Values;

namespace Douze.FileProcessors;

internal class LiquidTemplateFileProcessor : IFileProcessor
{
    public Predicate<FPath> Match { get; set; } = _ => true;

    public LiquidTemplateFileProcessor(Predicate<FPath> match)
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

            var fileContent = await sr.ReadToEndAsync();

            var template = options.FluidParser.Parse(fileContent);
            var context = new TemplateContext(document.Data, options.TemplateOptions);

            // Copy the data with locally created variables for captures
            var captures = new Dictionary<string, FluidValue>();

            context.Captured = (identifier, result) =>
            {
                document.Data.Add(identifier, result);
                return ValueTask.FromResult(result);
            };

            var content = await template.RenderAsync(context, HtmlEncoder.Default);

            result.Add(new DocumentResult($"Evaluating liquid for {document.Path}", new TextDocument(options.OutputFileSystem, document.Path, content, document.Data, document.ContentType)));
        });

        return result;
    }
}
