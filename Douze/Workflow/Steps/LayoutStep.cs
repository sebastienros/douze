using System.Text.Encodings.Web;
using Douze.FileSystems;
using Douze.Liquid;
using Douze.Nodes;
using Fluid;
using Fluid.Values;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: layout</c>. Renders the layout a file names in <c>layout</c> (resolved through the scopes, so folder and
/// site data can set it) with the file's content as <c>content</c>, then the layout named by that layout's front matter,
/// and so on. Names resolve through the file's metadata and scopes first, then the front matter of the layouts, the
/// innermost first.
/// </summary>
internal sealed class LayoutStep : Step, IMapStep
{
    public const int MaxDepth = 32;

    private static readonly GlobSet HtmlFiles = GlobSet.Parse(["**/*.html"]);

    private readonly FPath? _includes;
    private TemplateLoader? _loader;

    public LayoutStep(StepDefinition definition) : base(definition)
    {
        var options = new WithOptions(definition, "includes");
        _includes = options.GetString("includes") is { } includes ? StepPaths.Directory(includes) : (FPath?)null;
    }

    public override StepShape NativeShape => StepShape.Map;

    public override GlobSet DefaultMatch => HtmlFiles;

    /// <summary>
    /// The options of the <c>markdown</c> section, which tags read (the image defaults): a page laid out with other
    /// options is laid out again.
    /// </summary>
    public override Hash128 CacheVersion => _renderOptions;

    private Hash128 _renderOptions;

    public override ValueTask InitializeAsync(BuildContext context, CancellationToken ct)
    {
        _renderOptions = context.Site.Markdown.Hash;

        // The shared loader reads the site's includes folder; another folder gets its own loader.
        _loader = _includes is not { } includes || includes == context.Site.IncludesFolder
            ? context.Templates
            : new TemplateLoader(context.Source, [includes], context.Templates.Parser);

        return default;
    }

    public async ValueTask<FileList> ProcessAsync(VirtualFile file, StepContext context, CancellationToken ct)
    {
        if (!context.Scopes.TryResolve(file.Metadata, "layout", null, context.Dependencies, out var node, out _) || LayoutName(node) is not { } layout)
        {
            return FileList.Of(file);
        }

        var content = await file.Content.ReadTextAsync(ct);
        NodeObject? fallback = null;
        var chain = new List<string>();

        while (true)
        {
            chain.Add(layout);

            if (chain.Count > MaxDepth)
            {
                throw new DouzeException($"layout chain is longer than {MaxDepth}: {string.Join(" -> ", chain)}");
            }

            // A name without an extension is tried as is, then as .liquid, then as .html, like an include.
            var loaded = await _loader!.LoadAsync(new FPath(layout), context.Dependencies, ct);

            if (loaded == null && string.IsNullOrEmpty(Path.GetExtension(layout)))
            {
                loaded = await _loader.LoadAsync(new FPath(layout + ".liquid"), context.Dependencies, ct)
                    ?? await _loader.LoadAsync(new FPath(layout + ".html"), context.Dependencies, ct);
            }

            if (loaded == null)
            {
                throw new DouzeException($"layout '{layout}' not found in '{_includes ?? context.Build.Site.IncludesFolder}'{(file.SourcePath is { } source ? $" (used by '{source}')" : "")}");
            }

            // The front matter of an inner layout wins over the one of its own layout.
            fallback = Layer(loaded.FrontMatter, fallback);

            var templateContext = context.CreateTemplateContext(new MetadataValue(file.Metadata, fallback), file, ct);
            templateContext.SetValue("content", StringValue.Create(content, false));
            templateContext.AmbientValues[AmbientKeys.TemplatePath] = loaded.Path.FullName;

            content = await loaded.Template.Template.RenderAsync(templateContext, OutputEncoders.Html);

            if (LayoutName(loaded.FrontMatter["layout"]) is not { } next)
            {
                break;
            }

            layout = next;
        }

        return FileList.Of(file.With(Name, content: FileContent.FromText(content)));
    }

    private static string? LayoutName(Node? node)
    {
        return node is NodeValue<string> { Value: var name } && !string.IsNullOrWhiteSpace(name) ? name : null;
    }

    /// <summary>
    /// <paramref name="outer"/> overridden by <paramref name="inner"/>, without changing either.
    /// </summary>
    private static NodeObject Layer(NodeObject outer, NodeObject? inner)
    {
        if (inner == null || inner.Count == 0)
        {
            return outer;
        }

        var layered = outer.ShallowClone();

        foreach (var property in inner)
        {
            layered[property.Key] = property.Value;
        }

        return layered;
    }
}
