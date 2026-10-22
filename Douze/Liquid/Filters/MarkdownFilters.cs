using System.Net;
using System.Text;
using Douze.Liquid.Tags;
using Fluid;
using Fluid.Values;

namespace Douze.Liquid.Filters;

/// <summary>
/// Filters that render Markdown with the site's pipeline.
/// </summary>
internal static class MarkdownFilters
{
    /// <summary>
    /// The marker that ends an excerpt.
    /// </summary>
    public const string MoreMarker = "<!-- more -->";

    public static void Register(TemplateOptionsBuilder builder)
    {
        builder.AddFilter("markdownify", Markdownify);
        builder.AddFilter("strip_markdown", StripMarkdown);
        builder.AddFilter("excerpt", Excerpt);
    }

    /// <summary>
    /// <c>{{ text | markdownify }}</c>: the Markdown as HTML. <c>inline: true</c> returns a single paragraph without its
    /// <c>&lt;p&gt;</c> element.
    /// </summary>
    public static async ValueTask<FluidValue> Markdownify(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        if (input.IsNil())
        {
            return StringValue.Empty;
        }

        var inline = arguments["inline"].ToBooleanValue(context);
        var html = await TagRendering.RenderMarkdownAsync(input.ToStringValue(context), context, inline, "markdownify");

        return StringValue.Create(html, encode: false);
    }

    /// <summary>
    /// <c>{{ text | strip_markdown }}</c>: the text of the Markdown, without markup, entities decoded.
    /// </summary>
    public static async ValueTask<FluidValue> StripMarkdown(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        if (input.IsNil())
        {
            return StringValue.Empty;
        }

        var html = await TagRendering.RenderMarkdownAsync(input.ToStringValue(context), context, inline: false, "strip_markdown");

        return StringValue.Create(StripTags(html).Trim());
    }

    /// <summary>
    /// <c>{{ content | excerpt }}</c>: the Markdown before <c>&lt;!-- more --&gt;</c> as HTML, or without the marker,
    /// the first paragraph.
    /// </summary>
    public static async ValueTask<FluidValue> Excerpt(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        if (input.IsNil())
        {
            return StringValue.Empty;
        }

        var markdown = input.ToStringValue(context);
        var more = markdown.IndexOf(MoreMarker, StringComparison.Ordinal);

        if (more >= 0)
        {
            return StringValue.Create(await TagRendering.RenderMarkdownAsync(markdown[..more], context, inline: false, "excerpt"), encode: false);
        }

        var html = await TagRendering.RenderMarkdownAsync(markdown, context, inline: false, "excerpt");

        return StringValue.Create(FirstParagraph(html), encode: false);
    }

    /// <summary>
    /// The first <c>&lt;p&gt;</c> element of <paramref name="html"/>, or an empty string.
    /// </summary>
    internal static string FirstParagraph(string html)
    {
        var start = 0;

        while ((start = html.IndexOf("<p", start, StringComparison.Ordinal)) >= 0)
        {
            // <p> or <p class=...>, not <pre>.
            if (start + 2 < html.Length && html[start + 2] is '>' or ' ')
            {
                var end = html.IndexOf("</p>", start, StringComparison.Ordinal);
                return end < 0 ? "" : html[start..(end + 4)];
            }

            start += 2;
        }

        return "";
    }

    /// <summary>
    /// <paramref name="html"/> without its tags, entities decoded.
    /// </summary>
    internal static string StripTags(string html)
    {
        var builder = new StringBuilder(html.Length);
        var inTag = false;

        foreach (var c in html)
        {
            if (c == '<')
            {
                inTag = true;
            }
            else if (c == '>' && inTag)
            {
                inTag = false;
            }
            else if (!inTag)
            {
                builder.Append(c);
            }
        }

        return WebUtility.HtmlDecode(builder.ToString());
    }
}
