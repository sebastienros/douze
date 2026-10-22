using System.Collections.Immutable;
using Douze.FileSystems;
using Douze.Markdown;
using Douze.Nodes;
using Douze.Progress;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: markdown</c>. Renders Markdown to HTML with the site's pipeline (the <c>markdown</c> section of
/// <c>douze.yml</c>, <see cref="BuildContext.Markdown"/>) and changes the extension to <c>.html</c>, so that later
/// steps match the result on <c>**/*.html</c> and its content type is <c>text/html</c>. A file whose <c>markdown</c>
/// resolves to <c>false</c> (front matter, or folder or site data) passes through as it is. <c>with.extensions</c>
/// (<c>none</c>, <c>advanced</c>, or a list of Markdig extension names) replaces the extensions of the site's pipeline
/// for this step, keeping its other options.
/// </summary>
internal sealed class MarkdownStep : Step, IMapStep
{
    /// <summary>
    /// The key that turns Markdown off for a file: <c>markdown: false</c>.
    /// </summary>
    public const string OptOutKey = "markdown";

    private static readonly GlobSet MarkdownFiles = GlobSet.Parse(["**/*.md"]);

    private readonly ImmutableArray<string>? _extensions;
    private MarkdownRenderer? _renderer;

    public MarkdownStep(StepDefinition definition) : base(definition)
    {
        _ = new WithOptions(definition, "extensions");

        if (definition.With["extensions"] is not { } extensions)
        {
            return;
        }

        ImmutableArray<string> names = extensions switch
        {
            NodeValue<string> s => [.. s.Value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            NodeArray array when array.All(x => x is NodeValue<string>) => [.. array.Select(x => ((NodeValue<string>)x!).Value)],
            _ => throw new WorkflowValidationException($"option 'extensions' of step '{Name}' must be a string or a list of strings", definition.Location),
        };

        var errors = names.Select(MarkdownPipelineFactory.Validate).OfType<string>().ToList();

        if (errors.Count > 0)
        {
            throw new WorkflowValidationException([.. errors.Select(x => new WorkflowValidationError($"option 'extensions' of step '{Name}': {x}", definition.Location))]);
        }

        _extensions = names;
    }

    public override StepShape NativeShape => StepShape.Map;

    public override GlobSet DefaultMatch => MarkdownFiles;

    /// <summary>
    /// The Markdown options: a result rendered with other options is not reused.
    /// </summary>
    // Version 2: typographic apostrophes with smartypants.
    public override Hash128 CacheVersion => Hash128.Combine(_renderer?.Options.Hash ?? Hash128.Empty, Hash128.OfText("2"));

    public override ValueTask InitializeAsync(BuildContext context, CancellationToken ct)
    {
        _renderer = _extensions is { } extensions ? context.Markdown.WithExtensions(extensions) : context.Markdown;
        return default;
    }

    public async ValueTask<FileList> ProcessAsync(VirtualFile file, StepContext context, CancellationToken ct)
    {
        if (context.Scopes.TryResolve(file.Metadata, OptOutKey, null, context.Dependencies, out var optOut, out _) && optOut is NodeValue<bool> { Value: false })
        {
            return FileList.Of(file);
        }

        var renderer = _renderer ?? context.Build.Markdown;
        var markdown = await file.Content.ReadTextAsync(ct);
        var source = file.SourcePath ?? file.Path;

        var html = await renderer.RenderAsync(markdown, file.Metadata.Directory, context.Dependencies,
            warn: warning => context.Log(LogLevel.Warning, $"{source}({warning.Line},{warning.Column}): {warning.Message}"));

        var path = new FPath(Path.ChangeExtension(file.Path.FullName, ".html"));

        return FileList.Of(file.With(Name, path: path, content: FileContent.FromText(html)));
    }
}
