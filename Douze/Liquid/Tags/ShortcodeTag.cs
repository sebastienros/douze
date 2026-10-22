using System.Text.Encodings.Web;
using Douze.Markdown;
using Fluid;
using Fluid.Ast;
using Fluid.Values;

namespace Douze.Liquid.Tags;

/// <summary>
/// The render of a shortcode: the arguments and the body are evaluated with the page's context, the shortcode's
/// template is rendered with a <see cref="ShortcodeModel"/>, and in a Markdown page the output of a block shortcode
/// is written as a raw region.
/// </summary>
internal static class ShortcodeTag
{
    public const char RawRegionMarker = RawRegionExtension.Marker;
    public const char FencePlaceholderMarker = FenceTable.Marker;

    // Ambient values that describe the page's render, not the shortcode template's.
    private static readonly string[] PageOnlyAmbientValues = [AmbientKeys.Strict, AmbientKeys.Fences, AmbientKeys.Markdown, AmbientKeys.TemplatePath];

    /// <summary>
    /// A paired shortcode: the body is rendered to a string (with the page's fences restored); in a Markdown page it
    /// is then rendered to HTML (inline for <c>display: inline</c>), which is <c>body</c>, the string being
    /// <c>source</c>.
    /// </summary>
    public static async ValueTask<Completion> RenderBlockAsync(ShortcodeDefinition shortcode, TagArguments call, IReadOnlyList<Statement> statements, IFluidOutput output, TextEncoder encoder, TemplateContext context)
    {
        var (source, completion) = await TagRendering.RenderBodyAsync(statements, encoder, context);

        var body = TagRendering.IsMarkdown(context)
            ? await TagRendering.RenderMarkdownAsync(source, context, inline: shortcode.Display == ShortcodeDisplay.Inline, $"shortcode '{shortcode.Name}'")
            : source;

        await RenderAsync(shortcode, call, body, source, output, encoder, context);

        return completion;
    }

    public static async ValueTask<Completion> RenderTagAsync(ShortcodeDefinition shortcode, TagArguments call, IFluidOutput output, TextEncoder encoder, TemplateContext context)
    {
        await RenderAsync(shortcode, call, null, null, output, encoder, context);
        return Completion.Normal;
    }

    private static async ValueTask RenderAsync(ShortcodeDefinition shortcode, TagArguments call, string? body, string? source, IFluidOutput output, TextEncoder encoder, TemplateContext context)
    {
        var values = new Dictionary<string, FluidValue>(StringComparer.Ordinal);
        var positional = new List<FluidValue>();

        foreach (var argument in call.Arguments)
        {
            if (argument.Name == null)
            {
                positional.Add(await argument.Expression.EvaluateAsync(context));
            }
        }

        for (var i = 0; i < positional.Count && i < shortcode.ArgNames.Length; i++)
        {
            values[shortcode.ArgNames[i]] = positional[i];
        }

        // Named arguments win over positional ones of the same name.
        foreach (var argument in call.Arguments)
        {
            if (argument.Name != null)
            {
                values[argument.Name] = await argument.Expression.EvaluateAsync(context);
            }
        }

        values["args"] = ArrayValue.Create(positional);

        if (body != null)
        {
            values["body"] = StringValue.Create(body, encode: false);
            values["source"] = StringValue.Create(source!, encode: false);
        }

        TagRendering.Dependencies(context)?.RecordTemplate(shortcode.Path, shortcode.ContentHash);

        var page = context.AmbientValues.TryGetValue(AmbientKeys.Model, out var model) ? model as FluidValue : null;
        var templateContext = new TemplateContext(new ShortcodeModel(values, page), context.Options) { CancellationToken = context.CancellationToken };

        foreach (var (key, value) in context.AmbientValues)
        {
            if (!PageOnlyAmbientValues.Contains(key))
            {
                templateContext.AmbientValues[key] = value;
            }
        }

        // The template produces HTML: shortcodes it uses write their output as it is.
        templateContext.AmbientValues[AmbientKeys.TemplatePath] = shortcode.Path.FullName;

        string html;

        try
        {
            html = await shortcode.Template.Template.RenderAsync(templateContext, encoder);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var (position, reason) = e is TemplateRenderException render && render.Path == shortcode.Path.FullName
                ? ($"({render.Line + shortcode.LineOffset},{render.Column})", render.Reason)
                : ("", e.Message);

            throw new DouzeException($"shortcode '{shortcode.Name}' ({shortcode.Path}{position}): {reason}");
        }

        output.Write(TagRendering.IsMarkdown(context) && shortcode.Display == ShortcodeDisplay.Block ? RawRegionExtension.Encode(html) : html);
    }
}
