using Cysharp.Text;
using Douze.FileSystems;
using Douze.Results;
using Markdig;
using Markdig.Renderers;

namespace Douze.FileProcessors;

/// <summary>
/// Evaluates Markdown documents and changes the content type to text/html.
/// </summary>
internal sealed class MarkdownFileProcessor : IFileProcessor
{
    // Configure markdown
    private readonly MarkdownPipeline _markdownPipeline;

    public Predicate<FPath> Match { get; set; } = _ => true;

    public MarkdownFileProcessor(Predicate<FPath> match)
    {
        _markdownPipeline = new MarkdownPipelineBuilder()
            .UseYamlFrontMatter()
            .Build();

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

        var content = await ProcessContentAsync(fileContent);

        yield return new DocumentResult($"Converting markdown of {document.Path}", new TextDocument(options.OutputFileSystem, document.Path, content, document.Data, "text/html"));
    }

    public async ValueTask<string> ProcessContentAsync(string markdown)
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
