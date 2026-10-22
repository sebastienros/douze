using System.Text.Encodings.Web;
using Douze.Progress;
using Douze.Workflow;
using Fluid;
using Fluid.Ast;

namespace Douze.Liquid.Tags;

/// <summary>
/// What Douze's tags share: the ambient values of a render, rendering a body to a string, rendering Markdown with the
/// site's pipeline.
/// </summary>
internal static class TagRendering
{
    /// <summary>
    /// Whether the render produces Markdown source (<see cref="AmbientKeys.Markdown"/>).
    /// </summary>
    public static bool IsMarkdown(TemplateContext context) => context.AmbientValues.TryGetValue(AmbientKeys.Markdown, out var value) && value is true;

    public static StepContext? Step(TemplateContext context) => context.AmbientValues.TryGetValue(AmbientKeys.StepContext, out var value) ? value as StepContext : null;

    public static VirtualFile? File(TemplateContext context) => context.AmbientValues.TryGetValue(AmbientKeys.File, out var value) ? value as VirtualFile : null;

    public static DependencyTracker? Dependencies(TemplateContext context) => context.AmbientValues.TryGetValue(AmbientKeys.Dependencies, out var value) ? value as DependencyTracker : null;

    public static string? TemplatePath(TemplateContext context) => context.AmbientValues.TryGetValue(AmbientKeys.TemplatePath, out var value) ? value as string : null;

    /// <summary>
    /// The file being rendered, for messages, named like the errors of its unit.
    /// </summary>
    public static string PageName(TemplateContext context) => File(context) is { } file ? file.Path.FullName : TemplatePath(context) ?? "template";

    /// <summary>
    /// The step context of the render, which tags that use the build's services need.
    /// </summary>
    /// <exception cref="DouzeException">The render is not part of a build.</exception>
    public static StepContext RequireStep(TemplateContext context, string tag)
    {
        return Step(context) ?? throw new DouzeException($"'{tag}' can only be used in a template rendered by a step");
    }

    /// <summary>
    /// Renders <paramref name="statements"/> with <paramref name="context"/> to a string, and the protected fences of
    /// the page restored in it.
    /// </summary>
    public static async ValueTask<(string Text, Completion Completion)> RenderBodyAsync(IReadOnlyList<Statement> statements, TextEncoder encoder, TemplateContext context)
    {
        var output = new StringFluidOutput();
        var completion = Completion.Normal;

        foreach (var statement in statements)
        {
            completion = await statement.WriteToAsync(output, encoder, context);

            if (completion != Completion.Normal)
            {
                break;
            }
        }

        return (RestoreFences(output.ToString(), context), completion);
    }

    /// <summary>
    /// Puts the protected fences of the Markdown being rendered back in <paramref name="text"/>.
    /// </summary>
    public static string RestoreFences(string text, TemplateContext context)
    {
        return context.AmbientValues.TryGetValue(AmbientKeys.Fences, out var value) && value is FenceTable fences ? fences.Restore(text) : text;
    }

    /// <summary>
    /// Renders Markdown to HTML with the site's pipeline, with the page's folder for links and the unit's tracker.
    /// With <paramref name="inline"/>, a single paragraph is returned without its <c>&lt;p&gt;</c> element.
    /// </summary>
    public static async ValueTask<string> RenderMarkdownAsync(string markdown, TemplateContext context, bool inline, string tag)
    {
        var step = RequireStep(context, tag);
        var file = File(context);
        var page = PageName(context);

        var html = await step.Build.Markdown.RenderAsync(markdown, file?.Metadata.Directory, Dependencies(context) ?? step.Dependencies,
            warn: warning => step.Log(LogLevel.Warning, $"{page}: {tag}: {warning.Message}"));

        return inline ? Inline(html) : html;
    }

    /// <summary>
    /// The content of the paragraph when <paramref name="html"/> is a single paragraph, else the HTML as it is.
    /// </summary>
    public static string Inline(string html)
    {
        var trimmed = html.AsSpan().TrimEnd('\n');

        if (trimmed.StartsWith("<p>") && trimmed.EndsWith("</p>") && trimmed[3..].IndexOf("<p>") < 0)
        {
            return trimmed[3..^4].ToString();
        }

        return html;
    }

    /// <summary>
    /// Sets an ambient value until the lease is disposed, then restores the previous one.
    /// </summary>
    public static AmbientLease SetAmbient(TemplateContext context, string key, object? value) => new(context, key, value);

    internal readonly struct AmbientLease : IDisposable
    {
        private readonly TemplateContext _context;
        private readonly string _key;
        private readonly object? _previous;
        private readonly bool _had;

        public AmbientLease(TemplateContext context, string key, object? value)
        {
            _context = context;
            _key = key;
            _had = context.AmbientValues.TryGetValue(key, out _previous);

            if (value == null)
            {
                context.AmbientValues.Remove(key);
            }
            else
            {
                context.AmbientValues[key] = value;
            }
        }

        public void Dispose()
        {
            if (_had)
            {
                _context.AmbientValues[_key] = _previous!;
            }
            else
            {
                _context.AmbientValues.Remove(_key);
            }
        }
    }
}
