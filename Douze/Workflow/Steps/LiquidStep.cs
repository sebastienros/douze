using System.Text.Encodings.Web;
using Douze.FileSystems;
using Douze.Liquid;
using Douze.Liquid.Tags;
using Douze.Nodes;
using Fluid;
using Fluid.Values;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: liquid</c>. Without <c>with.template</c>, renders the content of each file as a Liquid template whose model
/// is the file's metadata, and keeps the variables it captures in its metadata; a file whose <c>liquid</c> resolves to
/// <c>false</c> (front matter, or folder or site data) passes through as it is. With <c>with.template</c> (a path in the
/// includes folder), renders that template once per file (<c>file</c> in the template) or once per group with
/// <c>group-by</c> (<c>group</c>), to the path rendered from <c>with.permalink</c>.
/// </summary>
/// <remarks>
/// In a Markdown file, fenced code blocks are not Liquid (<c>with.protect: fences</c>, the default; <c>none</c> turns
/// it off): they are replaced by placeholders before the content is parsed and restored in the output
/// (<see cref="FenceTable"/>). <c>with.strict: true</c> makes an undefined variable an error.
/// </remarks>
internal sealed class LiquidStep : Step, IMapStep, IGroupStep
{
    /// <summary>
    /// The key that turns Liquid off for a file: <c>liquid: false</c>.
    /// </summary>
    public const string OptOutKey = "liquid";

    private static readonly GlobSet Templates = GlobSet.Parse(["**/*.html", "**/*.md", "**/*.json"]);

    private readonly FPath? _template;
    private readonly string? _permalink;
    private readonly NodeObject? _data;
    private readonly TextEncoder _encoder;
    private readonly bool _protectFences;
    private readonly bool _strict;

    public LiquidStep(StepDefinition definition) : base(definition)
    {
        var options = new WithOptions(definition, "template", "permalink", "data", "encode", "protect", "strict");

        _template = options.GetString("template") is { } template ? new FPath(template).ToRelative() : (FPath?)null;
        _permalink = options.GetString("permalink");

        _encoder = options.GetString("encode", "html") switch
        {
            "html" => HtmlEncoder.Default,
            "none" => NullEncoder.Default,
            var other => throw new WorkflowValidationException($"option 'encode' of step '{Name}' must be 'html' or 'none', not '{other}'", definition.Location),
        };

        _protectFences = options.GetString("protect", "fences") switch
        {
            "fences" => true,
            "none" => false,
            var other => throw new WorkflowValidationException($"option 'protect' of step '{Name}' must be 'fences' or 'none', not '{other}'", definition.Location),
        };

        _strict = options.GetBool("strict");

        if (definition.With["data"] is { } data)
        {
            _data = data as NodeObject ?? throw new WorkflowValidationException($"option 'data' of step '{Name}' must be a mapping", definition.Location);
        }

        if (_template != null && _permalink == null)
        {
            throw new WorkflowValidationException($"step '{Name}' uses 'liquid' with 'template' but 'permalink' is missing", definition.Location);
        }

        if (_template == null && (_permalink != null || _data != null))
        {
            throw new WorkflowValidationException($"step '{Name}' uses 'liquid' with '{(_permalink != null ? "permalink" : "data")}' but 'template' is missing", definition.Location);
        }
    }

    public override StepShape NativeShape => StepShape.Map;

    public override bool SupportsGrouping => _template != null;

    public override GlobSet DefaultMatch => Templates;

    public async ValueTask<FileList> ProcessAsync(VirtualFile file, StepContext context, CancellationToken ct)
    {
        if (_template != null)
        {
            var model = await FileModelAsync(file, ct);
            var (path, content, metadata) = await RenderTemplateAsync("file", model, file.Metadata.Directory, file, context, ct);
            return FileList.Of(file.With(Name, path: path, content: content, metadata: metadata));
        }

        if (context.Scopes.TryResolve(file.Metadata, OptOutKey, null, context.Dependencies, out var optOut, out _) && optOut is NodeValue<bool> { Value: false })
        {
            return FileList.Of(file);
        }

        var text = await file.Content.ReadTextAsync(ct);
        var markdown = file.EffectiveContentType == "text/markdown";
        FenceTable? fences = null;
        var hash = file.ContentHash;

        if (markdown && _protectFences)
        {
            var protectedText = FenceTable.Protect(text, out fences);

            if (fences != null)
            {
                text = protectedText;
                hash = Hash128.OfText(text);
            }
        }

        var template = Parse(hash, text, file.Path.FullName, context);
        var templateContext = context.CreateTemplateContext(new MetadataValue(file.Metadata), file, ct);
        templateContext.AmbientValues[AmbientKeys.TemplatePath] = file.Path.FullName;

        if (markdown)
        {
            templateContext.AmbientValues[AmbientKeys.Markdown] = true;
        }

        if (fences != null)
        {
            templateContext.AmbientValues[AmbientKeys.Fences] = fences;
        }

        if (_strict)
        {
            templateContext.AmbientValues[AmbientKeys.Strict] = true;
        }

        // Captured variables ({% capture footer %}) are kept in the file's metadata, for its layout.
        NodeObject? captured = null;

        templateContext.Captured = (identifier, value, context) =>
        {
            var captureText = value.ToStringValue(context);
            (captured ??= new NodeObject())[identifier] = NodeValue.Create(fences?.Restore(captureText) ?? captureText);
            return ValueTask.FromResult(value);
        };

        var html = await template.Template.RenderAsync(templateContext, _encoder);

        if (fences != null)
        {
            html = fences.Restore(html);
        }

        var newMetadata = captured == null ? file.Metadata : file.Metadata.With(own =>
        {
            foreach (var property in captured)
            {
                own[property.Key] = property.Value;
            }
        });

        return FileList.Of(file.With(Name, content: FileContent.FromText(html), metadata: newMetadata));
    }

    public async ValueTask<StepOutput> ProcessGroupAsync(FileGroup group, StepContext context, CancellationToken ct)
    {
        var files = new NodeArray();

        foreach (var file in group.Files)
        {
            files.Add(await FileModelAsync(file, ct));
        }

        var model = new NodeObject { ["key"] = NodeValue.Create(group.Key), ["files"] = files };
        var (path, content, metadata) = await RenderTemplateAsync("group", model, group.Files[0].Metadata.Directory, null, context, ct);

        return new StepOutput(FileList.Of(VirtualFile.Derived(Name, group.Files, path, content, metadata, contentType: null)));
    }

    private async ValueTask<(FPath Path, FileContent Content, Metadata Metadata)> RenderTemplateAsync(string name, NodeObject model, FPath directory, VirtualFile? file, StepContext context, CancellationToken ct)
    {
        var loaded = await context.Build.Templates.LoadAsync(_template!.Value, context.Dependencies, ct)
            ?? throw new DouzeException($"template '{_template}' not found in '{context.Build.Site.IncludesFolder}'");

        // Names other than the unit resolve through the scopes, like site data in a page.
        var scopeModel = new MetadataValue(new Metadata(new NodeObject(), directory));

        var permalinkTemplate = context.Build.Templates.GetOrParse(Hash128.OfText(_permalink!), _permalink!, $"permalink of step '{Name}'");
        var permalinkContext = context.CreateTemplateContext(scopeModel, file, ct);
        permalinkContext.SetValue(name, new NodeObjectValue(model));
        var path = FPath.Combine(FPath.Root, await permalinkTemplate.Template.RenderAsync(permalinkContext, NullEncoder.Default));

        if (string.IsNullOrEmpty(Path.GetExtension(path.FullName)))
        {
            path = FPath.Combine(FPath.Root, path, "index.html");
        }

        var templateContext = context.CreateTemplateContext(scopeModel, file, ct);
        templateContext.SetValue(name, new NodeObjectValue(model));
        templateContext.AmbientValues[AmbientKeys.TemplatePath] = loaded.Path.FullName;

        if (_strict)
        {
            templateContext.AmbientValues[AmbientKeys.Strict] = true;
        }

        var content = await loaded.Template.Template.RenderAsync(templateContext, _encoder);

        var own = new NodeObject { ["url"] = NodeValue.Create(path.FullName) };

        if (_data != null)
        {
            foreach (var property in _data)
            {
                own[property.Key] = property.Value;
            }
        }

        return (path, FileContent.FromText(content), new Metadata(own, directory));
    }

    /// <summary>
    /// Parses a page; an unknown tag that is close to a shortcode name gets a suggestion.
    /// </summary>
    private static ParsedTemplate Parse(Hash128 hash, string text, string path, StepContext context)
    {
        try
        {
            return context.Build.Templates.GetOrParse(hash, text, path);
        }
        catch (TemplateParseException e) when (ShortcodeRegistry.SuggestTag(e.Reason, context.Build.Shortcodes.Shortcodes.Select(x => x.Name)) is { Length: > 0 } suggestion)
        {
            throw new TemplateParseException(e.Path, e.Line, e.Column, e.Reason + suggestion);
        }
    }

    private static async ValueTask<NodeObject> FileModelAsync(VirtualFile file, CancellationToken ct)
    {
        return new NodeObject
        {
            ["path"] = NodeValue.Create(file.Path.FullName),
            ["name"] = NodeValue.Create(Path.GetFileName(file.Path.FullName)),
            ["dir"] = NodeValue.Create(file.Path.GetDirectory().FullName),
            ["ext"] = NodeValue.Create(file.Path.GetExtensionWithDot()),
            ["content"] = file.Content.IsText ? NodeValue.Create(await file.Content.ReadTextAsync(ct)) : null,
            ["data"] = new NodeValue<Metadata>(file.Metadata),
        };
    }
}
