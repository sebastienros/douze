using Douze.Results;
using Fluid;
using Fluid.Values;
using Markdig;
using Markdig.Renderers;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;

namespace Douze
{
    internal class MarkdownFileProcessor : IFileProcessor
    {
        // Configure markdown
        readonly MarkdownPipeline _markdownPipeline;

        public MarkdownFileProcessor()
        {
            _markdownPipeline = new MarkdownPipelineBuilder()
            .UseYamlFrontMatter()
            .Build();
        }

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

            var destinationDirectoryName = Path.GetDirectoryName(destinationFilePath)!;
            var filePath = Path.GetFullPath(path);

            string parts = await FrontMatterHelper.MergeFrontMatterData(options, data, filePath);

            var template = options.FluidParser.Parse(parts);
            var context = new TemplateContext(data, options.TemplateOptions);
            var markdown = await template.RenderAsync(context, HtmlEncoder.Default);

            var document = Markdown.Parse(markdown, _markdownPipeline);
            string content = "";

            using (var writer = new StringWriter())
            {
                var renderer = new HtmlRenderer(writer);
                _markdownPipeline.Setup(renderer);

                renderer.Render(document);
                await writer.FlushAsync();

                content = writer.ToString();
            }

            // Copy the data with locally created variables for captures

            var captures = new Dictionary<string, FluidValue>();

            foreach (var name in context.ValueNames)
            {
                captures.Add(name, context.GetValue(name));
            }

            var html = await LayoutHelper.ProcessLayoutAsync(options, content, data, captures);

            yield return new TextResult(destinationFilePath, $"Converting to Markdown .{localFilePath}", html, "text/html");
        }
    }
}
