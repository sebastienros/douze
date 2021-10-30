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

    public async IAsyncEnumerable<IResult> ProcessFileAsync(IDocument document, Options options)
    {
        if (!Match(document.Path))
        {
            yield return new DocumentResult(document);
            yield break;
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

        yield return new DocumentResult($"Evaluating liquid for {document.Path}", new TextDocument(options.OutputFileSystem, document.Path, content, document.Data, document.ContentType));
    }
}
