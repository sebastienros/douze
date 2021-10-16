using Douze.Results;
using Fluid;
using Fluid.Values;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;

namespace Douze.FileProcessors
{
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
            var localFilePath = Path.GetFullPath(path).Substring(Path.GetFullPath(options.SourcePath).Length);

            // If the processed file is named "index.*" then we don't need to create a folder, otherwise the name
            // of the file is used as a folder such that the url contains it.
            // blog/post1.html -> blog/post1/index.html

            var destinationFilePath = Path.GetFileNameWithoutExtension(localFilePath) == "index"
                ? Path.Combine(localFilePath)
                : Path.Combine(Path.GetDirectoryName(localFilePath) ?? "", Path.GetFileNameWithoutExtension(localFilePath), "index.html")
                ;

            destinationFilePath = Path.ChangeExtension(destinationFilePath, "html");

            var filePath = Path.GetFullPath(path);

            string parts = await FrontMatterHelper.MergeFrontMatterData(options, data, filePath);

            var template = options.FluidParser.Parse(parts);
            var context = new TemplateContext(data, options.TemplateOptions);
            var source = await template.RenderAsync(context, HtmlEncoder.Default);
            string content = await ProcessContent(source);

            // Copy the data with locally created variables for captures

            var captures = new Dictionary<string, FluidValue>();

            foreach (var name in context.ValueNames)
            {
                captures.Add(name, context.GetValue(name));
            }

            var html = await LayoutHelper.ProcessLayoutAsync(options, content, data, captures);

            yield return new TextResult(destinationFilePath, $"Generating {Format} file .{localFilePath}", html, "text/html");
        }

        public abstract Task<string> ProcessContent(string content);
    }
}
