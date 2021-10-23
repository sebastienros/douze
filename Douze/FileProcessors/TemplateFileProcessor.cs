using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
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
    public abstract string Format { get; }

    public async IAsyncEnumerable<IResult> ProcessFileAsync(FileEntry file, Options options, JsonObject data)
    {
        var parts = await FrontMatterHelper.MergeFrontMatterData(options, data, file);
        var metadata = await file.FileSystem.GetFileMetadaAsync(file.Path);

        var page = data["page"] as JsonObject;

        if (page == null)
        {
            data["page"] = page = new JsonObject();
        }

        page.Add("date", metadata.LastWriteTimeUtc);

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
        var content = await ProcessContentAsync(source);

        // Apply layout

        var html = await LayoutHelper.ProcessLayoutAsync(options, content, data, captures);

        // Compute destination file path, e.g. blog/post1/index.html

        var localFilePath = file.Path;
        var extension = "html";

        if (data.TryGetPropertyValue("permalink", out var permalink) && permalink != null && !string.IsNullOrWhiteSpace(permalink.GetValue<string>()))
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

        yield return new TextResult(localFilePath, $"Generating {Format} file {localFilePath}", html, "text/html");
    }

    public abstract ValueTask<string> ProcessContentAsync(string content);
}
