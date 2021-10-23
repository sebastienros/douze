using System.Text.Encodings.Web;
using Douze.FileSystems;
using Douze.Results;
using Fluid;
using Fluid.Values;

namespace Douze.FileProcessors;

/// <summary>
/// Implements
/// - FrontMatter
/// - Liquid pre-processing
/// - Layout chaining
/// - Permalink
/// </summary>
internal abstract class TemplateFileProcessor : IFileProcessor
{
    public Predicate<FPath>? Match { get; set; }

    public async IAsyncEnumerable<IResult> ProcessFileAsync(IDocument document, Options options)
    {
        if (Match != null && !Match(document.Path))
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

        context.Captured = async (identifier, result) =>
        {
            result = await ProcessContentAsync(result);
            captures.Add(identifier, new StringValue(result, false));
            return result;
        };

        var source = await template.RenderAsync(context, HtmlEncoder.Default);
        var content = await ProcessContentAsync(source);

        // Apply layout

        var html = await LayoutHelper.ProcessLayoutAsync(options, content, document.Data, captures);

        // Compute destination file path, e.g. blog/post1/index.html

        var localFilePath = document.Path;
        var extension = "html";

        if (document.Data.TryGetPropertyValue("permalink", out var permalink) && permalink != null && !string.IsNullOrWhiteSpace(permalink.GetValue<string>()))
        {
            var permalinkTemplate = options.FluidParser.Parse(permalink.ToString());
            localFilePath = FPath.Combine("/", await permalinkTemplate.RenderAsync(context));

            extension = Path.GetExtension(localFilePath.FullName) ?? ".";
        }

        // If the processed file is named "index.*" then we don't need to create a folder, otherwise the name
        // of the file is used as a folder such that the url contains it.
        // blog/post1.html -> blog/post1/index.html

        if (Path.GetFileNameWithoutExtension(localFilePath.FullName) != "index")
        {
            localFilePath = FPath.Combine("/", localFilePath.GetDirectory(), Path.GetFileNameWithoutExtension(localFilePath.FullName) ?? "", "index.html");
        }

        localFilePath = Path.ChangeExtension(localFilePath.FullName, extension);


        yield return new DocumentResult($"Generating file {localFilePath}", new TextDocument(options.OutputFileSystem, localFilePath, html, document.Data, "text/html"));
    }

    public abstract ValueTask<string> ProcessContentAsync(string content);
}
