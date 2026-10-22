using System.Text;
using Douze.Markdown;
using Fluid;
using Fluid.Values;

namespace Douze.Liquid.Filters;

/// <summary>
/// Filters that produce or transform HTML.
/// </summary>
internal static class HtmlFilters
{
    public static void Register(TemplateOptionsBuilder builder)
    {
        builder.AddFilter("toc", Toc);
    }

    /// <summary>
    /// <c>{{ toc | toc: class: "toc", max-depth: 3 }}</c>: the tree of the <c>headings</c> step as nested lists,
    /// <c>&lt;ul class="toc"&gt;&lt;li&gt;&lt;a href="#id"&gt;text&lt;/a&gt;&lt;ul&gt;...&lt;/ul&gt;&lt;/li&gt;&lt;/ul&gt;</c>.
    /// Entries deeper than <c>max-depth</c> are left out with their children; an empty tree is an empty string.
    /// </summary>
    public static async ValueTask<FluidValue> Toc(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        if (input.Type != FluidValues.Array)
        {
            return StringValue.Empty;
        }

        var cssClass = arguments["class"] is { Type: not FluidValues.Nil } c ? c.ToStringValue(context) : null;
        var maxDepth = arguments["max-depth"] is { Type: not FluidValues.Nil } d ? (int)d.ToNumberValue(context) : 6;

        var builder = new StringBuilder();
        await WriteListAsync(builder, input, cssClass, maxDepth, context);

        return StringValue.Create(builder.ToString(), encode: false);
    }

    private static async ValueTask WriteListAsync(StringBuilder builder, FluidValue entries, string? cssClass, int maxDepth, TemplateContext context)
    {
        var started = false;

        await foreach (var entry in entries.EnumerateAsync(context))
        {
            var depth = (await entry.GetValueAsync("depth", context)).ToNumberValue(context);

            if (depth > maxDepth)
            {
                continue;
            }

            if (!started)
            {
                builder.Append("<ul");

                if (!string.IsNullOrEmpty(cssClass))
                {
                    builder.Append(" class=\"");
                    builder.Append(HeadingScanner.EncodeAttribute(cssClass));
                    builder.Append('"');
                }

                builder.Append('>');
                started = true;
            }

            var id = (await entry.GetValueAsync("id", context)).ToStringValue(context);
            var text = (await entry.GetValueAsync("text", context)).ToStringValue(context);

            builder.Append("<li><a href=\"#");
            builder.Append(HeadingScanner.EncodeAttribute(id));
            builder.Append("\">");
            builder.Append(HeadingScanner.EncodeAttribute(text));
            builder.Append("</a>");

            var children = await entry.GetValueAsync("children", context);

            if (children.Type == FluidValues.Array)
            {
                await WriteListAsync(builder, children, null, maxDepth, context);
            }

            builder.Append("</li>");
        }

        if (started)
        {
            builder.Append("</ul>");
        }
    }
}
