using Cysharp.Text;
using Douze.FileSystems;
using Markdig;
using Markdig.Renderers;

namespace Douze.FileProcessors;

internal sealed class MarkdownFileProcessor : TemplateFileProcessor
{
    // Configure markdown
    private readonly MarkdownPipeline _markdownPipeline;

    public MarkdownFileProcessor(Predicate<FPath>? match)
    {
        _markdownPipeline = new MarkdownPipelineBuilder()
            .UseYamlFrontMatter()
            .Build();

        Match = match;
    }

    public override async ValueTask<string> ProcessContentAsync(string markdown)
    {
        var document = Markdown.Parse(markdown, _markdownPipeline);

        await using var writer = new ZStringWriter();
        var renderer = new HtmlRenderer(writer);
        _markdownPipeline.Setup(renderer);

        renderer.Render(document);
        await writer.FlushAsync();

        var content = writer.ToString();

        return content;
    }
}
