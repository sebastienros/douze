using System.Text.Encodings.Web;
using Douze.Progress;
using Fluid;
using Fluid.Ast;

namespace Douze.Liquid.Tags;

/// <summary>
/// <c>{% error "message" %}</c> fails the unit with the page and the tag's position; <c>{% warn "message" %}</c> logs
/// a warning. The message is any Liquid expression, with filters.
/// </summary>
internal static class DiagnosticTags
{
    public static readonly string[] Names = ["error", "warn"];

    public static void Register(DouzeParser parser)
    {
        parser.RegisterParserTag("error", parser.TagExpression, ErrorAsync);
        parser.RegisterParserTag("warn", parser.TagExpression, WarnAsync);
    }

    private static async ValueTask<Completion> ErrorAsync(TagExpression tag, IFluidOutput output, TextEncoder encoder, TemplateContext context)
    {
        var message = (await tag.Expression.EvaluateAsync(context)).ToStringValue(context);
        throw new TemplateRenderException(TagRendering.TemplatePath(context), tag.Position.Line, tag.Position.Column, message);
    }

    private static async ValueTask<Completion> WarnAsync(TagExpression tag, IFluidOutput output, TextEncoder encoder, TemplateContext context)
    {
        var message = (await tag.Expression.EvaluateAsync(context)).ToStringValue(context);

        if (TagRendering.Step(context) is { } step)
        {
            var page = TagRendering.PageName(context);
            var template = TagRendering.TemplatePath(context) ?? page;
            var position = $"({tag.Position.Line},{tag.Position.Column})";

            step.Log(LogLevel.Warning, template == page ? $"{page}{position}: {message}" : $"{page}: {template}{position}: {message}");
        }

        return Completion.Normal;
    }
}
