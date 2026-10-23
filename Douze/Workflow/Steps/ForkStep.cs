using Douze.FileSystems;
using Douze.Liquid;
using Douze.Nodes;
using Fluid;
using Fluid.Values;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: fork</c>. Passes each file through and emits a copy of it at <c>with.permalink</c>, a Liquid template
/// rendered with the file's model (a result without an extension is a folder: <c>index.html</c> is added). The copy's
/// own metadata is the file's with <c>with.data</c> merged over it and <c>url</c> set to the copy's path; its content is
/// the file's, shared, or <c>with.content</c> rendered with the file's model and the file's text as <c>content</c>.
/// With <c>with.strip-regions</c> (default true), the lines that are a raw region marker (a single U+001E character)
/// are removed from the copy's text. A file for which <c>with.skip</c> (a Liquid expression) is true gets no copy.
/// <c>with.strict: true</c> makes an undefined variable of the permalink or the content an error. A permalink with an
/// empty segment or an empty file name (<c>/.txt</c>, <c>/a//b.md</c>: a value that rendered nothing) is an error.
/// </summary>
/// <remarks>
/// Templates render without HTML encoding: a path, a Markdown or a text copy must keep its characters.
/// </remarks>
internal sealed class ForkStep : Step, IMapStep
{
    /// <summary>
    /// The line that delimits a raw region in a page's text.
    /// </summary>
    public const char RegionMarker = '\u001E';

    private readonly string _permalink;
    private readonly string? _content;
    private readonly NodeObject? _data;
    private readonly bool _stripRegions;
    private readonly string? _skip;
    private readonly bool _strict;

    public ForkStep(StepDefinition definition) : base(definition)
    {
        var options = new WithOptions(definition, "permalink", "data", "content", "strip-regions", "skip", "strict");
        _permalink = options.GetRequiredString("permalink");
        _skip = options.GetString("skip");
        _content = options.GetString("content");
        _stripRegions = options.GetBool("strip-regions", defaultValue: true);
        _strict = options.GetBool("strict");

        if (definition.With["data"] is { } data)
        {
            _data = data as NodeObject ?? throw new WorkflowValidationException($"option 'data' of step '{Name}' must be a mapping", definition.Location);
        }
    }

    public override StepShape NativeShape => StepShape.Map;

    public async ValueTask<FileList> ProcessAsync(VirtualFile file, StepContext context, CancellationToken ct)
    {
        var model = new MetadataValue(file.Metadata);

        if (_skip != null)
        {
            var skipTemplate = context.Build.Templates.GetOrParse(Hash128.OfText(_skip), $"{{% if {_skip} %}}true{{% endif %}}", $"skip of step '{Name}'");

            if (await skipTemplate.Template.RenderAsync(context.CreateTemplateContext(model, file, ct), NullEncoder.Default) == "true")
            {
                return FileList.Of(file);
            }
        }

        var permalinkTemplate = context.Build.Templates.GetOrParse(Hash128.OfText(_permalink), _permalink, $"permalink of step '{Name}'");
        var rendered = (await permalinkTemplate.Template.RenderAsync(CreateTemplateContext(model, file, context, ct), NullEncoder.Default)).Trim();

        if (rendered.Length == 0)
        {
            throw new DouzeException($"the permalink of step '{Name}' is empty for '{file.SourcePath ?? file.Path}'");
        }

        if (EmptyPart(rendered) is { } part)
        {
            throw new DouzeException($"the permalink of step '{Name}' is '{rendered}' for '{file.SourcePath ?? file.Path}': it has an empty {part}, a value of the template is probably undefined");
        }

        var path = FPath.Combine(FPath.Root, rendered);

        if (string.IsNullOrEmpty(Path.GetExtension(path.FullName)))
        {
            path = FPath.Combine(FPath.Root, path, "index.html");
        }

        var content = file.Content;

        if (_content != null || (_stripRegions && file.Content.IsText))
        {
            var original = file.Content.IsText ? await file.Content.ReadTextAsync(ct) : "";
            var text = original;

            if (_content != null)
            {
                var contentTemplate = context.Build.Templates.GetOrParse(Hash128.OfText(_content), _content, $"content of step '{Name}'");
                var templateContext = CreateTemplateContext(model, file, context, ct);
                templateContext.SetValue("content", StringValue.Create(text));
                text = await contentTemplate.Template.RenderAsync(templateContext, NullEncoder.Default);
            }

            if (_stripRegions)
            {
                text = StripRegions(text);
            }

            // The same text keeps the same content instance.
            content = ReferenceEquals(text, original) ? file.Content : FileContent.FromText(text);
        }

        var metadata = file.Metadata.With(own =>
        {
            if (_data != null)
            {
                FrontMatterStep.MergeInto(own, _data);
            }

            own["url"] = NodeValue.Create(path.FullName);
        });

        return FileList.Of([file, file.With(Name, path: path, content: content, metadata: metadata)]);
    }

    private TemplateContext CreateTemplateContext(MetadataValue model, VirtualFile file, StepContext context, CancellationToken ct)
    {
        var templateContext = context.CreateTemplateContext(model, file, ct);

        if (_strict)
        {
            templateContext.AmbientValues[AmbientKeys.Strict] = true;
        }

        return templateContext;
    }

    /// <summary>
    /// The part of a rendered permalink that is empty, or null: a segment (<c>/a//b.md</c>), or the file name before
    /// its extension (<c>/.txt</c>). A trailing <c>/</c> is a folder.
    /// </summary>
    internal static string? EmptyPart(string permalink)
    {
        var trimmed = permalink.Replace('\\', '/').Trim('/');

        if (trimmed.Length == 0)
        {
            return null;
        }

        var segments = trimmed.Split('/');

        if (segments.Any(x => x.Length == 0))
        {
            return "segment";
        }

        var name = segments[^1];
        return !permalink.EndsWith('/') && name.LastIndexOf('.') == 0 ? "file name" : null;
    }

    /// <summary>
    /// Removes the lines that are exactly <see cref="RegionMarker"/>; returns <paramref name="text"/> itself when it has none.
    /// </summary>
    internal static string StripRegions(string text)
    {
        if (text.IndexOf(RegionMarker) < 0)
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        var start = 0;

        while (start < text.Length)
        {
            var end = text.IndexOf('\n', start);
            var next = end < 0 ? text.Length : end + 1;
            var line = text.AsSpan(start, (end < 0 ? text.Length : end) - start);

            if (line is [RegionMarker] or [RegionMarker, '\r'])
            {
                start = next;
                continue;
            }

            builder.Append(text, start, next - start);
            start = next;
        }

        return builder.ToString();
    }
}
