using Markdig;
using Markdig.Renderers;

namespace Douze.FileProcessors
{
    internal sealed class MarkdownFileProcessor : TemplateFileProcessor
    {
        // Configure markdown
        readonly MarkdownPipeline _markdownPipeline;

        public MarkdownFileProcessor()
        {
            _markdownPipeline = new MarkdownPipelineBuilder()
            .UseYamlFrontMatter()
            .Build();
        }

        public override string Format => "Markdown";

        public override async ValueTask<string> ProcessContentAsync(string markdown)
        {
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

            return content;
        }
    }
}
