using Douze.Progress;
using Douze.Workflow;
using Fluid;
using Fluid.Values;

namespace Douze.Liquid.Filters;

/// <summary>
/// What the filters of Douze share: names resolved like a template does (locals, then the model, so that the reads are
/// recorded as dependencies), the site's locales, expressions, messages.
/// </summary>
internal static class FilterHelpers
{
    /// <summary>
    /// Resolves a name like <c>{{ name }}</c>: an assigned variable, else the model (the page's metadata and scopes,
    /// which records the read).
    /// </summary>
    public static async ValueTask<FluidValue> ResolveAsync(TemplateContext context, string name)
    {
        var local = context.GetValue(name);

        if (local is not UndefinedValue && !local.IsNil())
        {
            return local;
        }

        return context.Model.IsNil() ? NilValue.Instance : await context.Model.GetValueAsync(name, context);
    }

    /// <summary>
    /// Reads a dotted path (<c>data.title</c>) from <paramref name="value"/>, member by member.
    /// </summary>
    public static async ValueTask<FluidValue> GetPathAsync(FluidValue value, string path, TemplateContext context)
    {
        if (path.Length == 0)
        {
            return value;
        }

        foreach (var segment in path.Split('.'))
        {
            if (value.IsNil())
            {
                return NilValue.Instance;
            }

            value = await value.GetValueAsync(segment, context);
        }

        return value;
    }

    /// <summary>
    /// The site's locales (<see cref="AmbientKeys.Locales"/>). The render depends on them: <c>site.locales</c> is read
    /// whole through the model, so that a change of the settings renders the template again.
    /// </summary>
    public static async ValueTask<LocaleSet> GetLocalesAsync(TemplateContext context)
    {
        var locales = context.AmbientValues.TryGetValue(AmbientKeys.Locales, out var value) && value is LocaleSet set ? set : LocaleSet.None;

        var site = await ResolveAsync(context, "site");
        var settings = site.IsNil() ? NilValue.Instance : await site.GetValueAsync("locales", context);

        if (settings is NodeObjectValue obj)
        {
            obj.ReadAll();
        }

        return locales;
    }

    /// <summary>
    /// Reports a warning once per build for <paramref name="message"/>, through the step that renders.
    /// </summary>
    public static void WarnOnce(TemplateContext context, string message)
    {
        if (context.AmbientValues.TryGetValue(AmbientKeys.StepContext, out var value) && value is StepContext step)
        {
            step.Build.WarnOnce(step.StepName, message);
        }
    }

    /// <summary>
    /// The string of a value, or null for nil.
    /// </summary>
    public static string? AsString(FluidValue value, TemplateContext context)
    {
        return value.IsNil() || value is UndefinedValue ? null : value.ToStringValue(context);
    }

    /// <summary>
    /// Evaluates a Liquid expression with <paramref name="variable"/> set to <paramref name="item"/>: as a condition
    /// (<c>{% if %}</c>), or as a value written as text.
    /// </summary>
    public static async ValueTask<string> EvaluateAsync(TemplateContext context, string variable, FluidValue item, string expression, bool condition)
    {
        var text = condition ? "{% if " + expression + " %}1{% endif %}" : "{{ " + expression + " }}";
        var template = Parse(context, text);

        using (context.EnterScope(ScopeBehavior.Local))
        {
            context.SetValue(variable, item);
            return await template.RenderAsync(context, NullEncoder.Default, isolateContext: false);
        }
    }

    private static IFluidTemplate Parse(TemplateContext context, string text)
    {
        if (context.Options.FileProvider is TemplateLoader loader)
        {
            return loader.GetOrParse(Hash128.OfText(text), text, "expression").Template;
        }

        return Fallback.Value.GetOrParse(Hash128.OfText(text), text, "expression").Template;
    }

    // Parses expressions for the renders that have no template loader (tests).
    private static readonly Lazy<TemplateLoader> Fallback = new(() => new TemplateLoader(new FileSystems.MemoryFileSystem(), [], LiquidOptions.CreateParser()));
}
