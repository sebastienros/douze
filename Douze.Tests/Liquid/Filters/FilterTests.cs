using Douze.FileSystems;
using Douze.Liquid;
using Douze.Nodes;
using Douze.Workflow;
using Douze.Workflow.Yaml;
using Fluid;

namespace Douze.Tests.Liquid.Filters;

/// <summary>
/// Renders templates with the filters of Douze, a page's metadata as the model, scopes and locales.
/// </summary>
public abstract class FilterTests
{
    private readonly TemplateLoader _loader = new(new MemoryFileSystem(), ["/_includes/"], LiquidOptions.CreateParser());
    private TemplateOptions? _options;

    internal async Task<string> RenderAsync(string template, Metadata? metadata = null, ScopeSet? scopes = null, LocaleSet? locales = null, DependencyTracker? deps = null)
    {
        _options ??= LiquidOptions.Create(_loader);

        var context = new TemplateContext(new MetadataValue(metadata ?? new Metadata(new NodeObject(), "/")), _options);
        context.AmbientValues[AmbientKeys.Scopes] = scopes ?? ScopeSet.Empty;
        context.AmbientValues[AmbientKeys.Dependencies] = deps ?? DependencyTracker.Null;
        context.AmbientValues[AmbientKeys.Locales] = locales ?? LocaleSet.None;

        var parsed = _loader.GetOrParse(Hash128.OfText(template), template, "test");
        return await parsed.Template.RenderAsync(context, NullEncoder.Default);
    }

    /// <summary>
    /// The locales of a <c>site.locales</c> section, under the content folder <c>/</c>.
    /// </summary>
    internal static LocaleSet Locales(string yaml)
    {
        return WorkflowLoader.Load("site:\n  locales:\n" + string.Join("\n", yaml.Split('\n').Select(x => "    " + x)), "douze.yml").Site.Locales!.WithContentFolder(FPath.Root);
    }

    /// <summary>
    /// Scopes with <paramref name="data"/> as site data and the locales' settings under <c>site.locales</c>.
    /// </summary>
    internal static ScopeSet Scopes(NodeObject data, LocaleSet? locales = null)
    {
        var settings = locales is { IsEnabled: true } ? new NodeObject { ["site"] = new NodeObject { ["locales"] = locales.ToNode() } } : new NodeObject();
        return ScopeSet.FromSettings(settings).WithSite(new SiteScope("data", data));
    }

    internal static Metadata Page(string directory = "/", params (string Key, Node? Value)[] values)
    {
        var own = new NodeObject();

        foreach (var (key, value) in values)
        {
            own[key] = value;
        }

        return new Metadata(own, directory);
    }

    internal static Node? Yaml(string yaml) => YamlHelper.Parse(yaml);
}
