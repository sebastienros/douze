using Douze.FileSystems;
using Douze.Results;
using Fluid;

namespace Douze.FileProcessors;

/// <summary>
/// Computes the file name of the destination file.
/// If the permalink contains a filename with extension it is preserved.
/// If the permalink doesn't contain and extension it is assumed as the final endpoint and uses index.html.
/// </summary>
internal class PermalinkFileProcessor : IFileProcessor
{
    public Predicate<FPath> Match { get; set; } = _ => true;

    public PermalinkFileProcessor(Predicate<FPath> match)
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

        // Compute destination file path, e.g. blog/post1/index.html

        var context = new TemplateContext(document.Data, options.TemplateOptions);

        var localFilePath = document.Path;

        var defaultExtension = Path.GetExtension(document.Path.FullName);

        if (document.Data.TryGetPropertyValue("permalink", out var permalink) && permalink != null && !string.IsNullOrWhiteSpace(permalink.GetValue<string>()))
        {
            var permalinkTemplate = options.FluidParser.Parse(permalink.ToString());
            localFilePath = FPath.Combine("/", await permalinkTemplate.RenderAsync(context));
        }

        var extension = Path.GetExtension(localFilePath.FullName);

        if (String.IsNullOrEmpty(extension))
        {
            localFilePath = FPath.Combine("/", localFilePath, "index.html");
        }

        yield return new DocumentResult($"Generating file {localFilePath}", new CopyDocument(document, options.OutputFileSystem, localFilePath));
    }
}
