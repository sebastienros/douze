using Douze.Results;
using Fluid;
using Fluid.Values;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;

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
    public abstract string Format { get; }

    public async IAsyncEnumerable<IResult> ProcessFileAsync(string path, Options options, JsonObject data)
    {
        string parts = await FrontMatterHelper.MergeFrontMatterData(options, data, path);

        // Add page information
        var page = new JsonObject();
        data["page"] = page;
        page.Add("date", new FileInfo(Path.GetFullPath(path)).CreationTime);

        var template = options.FluidParser.Parse(parts);
        var context = new TemplateContext(data, options.TemplateOptions);

        // Copy the data with locally created variables for captures
        var captures = new Dictionary<string, FluidValue>();

        context.Captured = async (identifier, result) =>
        {
            result = await ProcessContentAsync(result);
            captures.Add(identifier, new StringValue(result, false));
            return result;
        };

        var source = await template.RenderAsync(context, HtmlEncoder.Default);
        string content = await ProcessContentAsync(source);


        // Apply layout

        var html = await LayoutHelper.ProcessLayoutAsync(options, content, data, captures);

        // Compute destination file path, e.g. blog/post1/index.html

        string localFilePath = path.Substring(Path.GetFullPath(options.SourcePath).Length);
        string extension = "html";

        if (data.TryGetPropertyValue("permalink", out var permalink) && permalink != null && !string.IsNullOrWhiteSpace(permalink.ToString()))
        {
            var permalinkTemplate = options.FluidParser.Parse(permalink.ToString());
            localFilePath = await permalinkTemplate.RenderAsync(context);

            extension = Path.GetExtension(localFilePath);
        }

        // If the processed file is named "index.*" then we don't need to create a folder, otherwise the name
        // of the file is used as a folder such that the url contains it.
        // blog/post1.html -> blog/post1/index.html

        if (Path.GetFileNameWithoutExtension(localFilePath) != "index")
        {
            localFilePath = Path.Combine(Path.GetDirectoryName(localFilePath) ?? "", Path.GetFileNameWithoutExtension(localFilePath), "index.html");
        }

        localFilePath = Path.ChangeExtension(localFilePath, extension);

        yield return new TextResult(localFilePath, $"Generating {Format} file {localFilePath}", html, "text/html");
    }

    public abstract ValueTask<string> ProcessContentAsync(string content);
}
