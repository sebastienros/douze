using Cysharp.Text;
using Douze.FileSystems;
using Douze.Nodes;
using Markdig;
using Markdig.Renderers;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: markdown</c>. Renders Markdown to HTML and changes the extension to <c>.html</c>, so that later steps match
/// the result on <c>**/*.html</c> and its content type is <c>text/html</c>. <c>with.extensions</c>: <c>none</c> (default), <c>advanced</c>, or a list of Markdig
/// extension names (<c>pipetables</c>, <c>footnotes</c>, ...).
/// </summary>
internal sealed class MarkdownStep : Step, IMapStep
{
    private static readonly GlobSet MarkdownFiles = GlobSet.Parse(["**/*.md"]);

    private readonly MarkdownPipeline _pipeline;

    public MarkdownStep(StepDefinition definition) : base(definition)
    {
        _ = new WithOptions(definition, "extensions");

        var builder = new MarkdownPipelineBuilder();

        if (definition.With["extensions"] is { } extensions)
        {
            var names = extensions switch
            {
                NodeValue<string> s => s.Value,
                NodeArray array when array.All(x => x is NodeValue<string>) => string.Join('+', array.Select(x => ((NodeValue<string>)x!).Value)),
                _ => throw new WorkflowValidationException($"option 'extensions' of step '{Name}' must be a string or a list of strings", definition.Location),
            };

            if (names != "none")
            {
                try
                {
                    builder.Configure(names);
                }
                catch (ArgumentException e)
                {
                    throw new WorkflowValidationException($"option 'extensions' of step '{Name}': {e.Message}", definition.Location);
                }
            }
        }

        _pipeline = builder.Build();
    }

    public override StepShape NativeShape => StepShape.Map;

    public override GlobSet DefaultMatch => MarkdownFiles;

    public async ValueTask<FileList> ProcessAsync(VirtualFile file, StepContext context, CancellationToken ct)
    {
        var markdown = await file.Content.ReadTextAsync(ct);
        var document = Markdown.Parse(markdown, _pipeline);

        using var writer = new ZStringWriter();
        var renderer = new HtmlRenderer(writer);
        _pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();

        var path = new FPath(Path.ChangeExtension(file.Path.FullName, ".html"));

        return FileList.Of(file.With(Name, path: path, content: FileContent.FromText(writer.ToString())));
    }
}
