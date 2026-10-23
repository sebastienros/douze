using System.Globalization;
using System.Text;
using Fluid;
using Fluid.Values;

namespace Douze.Liquid.Filters;

/// <summary>
/// Filters of feeds, sitemaps and text exports: <c>xml_escape</c>, <c>absolute_url</c>, <c>relative_url</c>,
/// <c>strip_html</c>, <c>date_to_xmlschema</c> and <c>date_to_rfc822</c>.
/// </summary>
internal static class OutputFilters
{
    public static void Register(TemplateOptionsBuilder builder)
    {
        builder
            .AddFilter("xml_escape", XmlEscape)
            .AddFilter("absolute_url", AbsoluteUrl)
            .AddFilter("relative_url", RelativeUrl)
            .AddFilter("strip_html", StripHtml)
            .AddFilter("date_to_xmlschema", DateToXmlSchema)
            .AddFilter("date_to_rfc822", DateToRfc822);
    }

    /// <summary>
    /// <c>{{ title | xml_escape }}</c>: <c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c>, <c>"</c> and <c>'</c> as entities.
    /// The result is escaped already, which the output does not encode again.
    /// </summary>
    public static ValueTask<FluidValue> XmlEscape(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        var text = input.ToStringValue(context);

        if (text.AsSpan().IndexOfAny("&<>\"'") < 0)
        {
            return StringValue.Create(text, encode: false);
        }

        var builder = new StringBuilder(text.Length + 16);

        foreach (var c in text)
        {
            builder.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#39;",
                _ => null,
            } ?? c.ToString());
        }

        return StringValue.Create(builder.ToString(), encode: false);
    }

    /// <summary>
    /// <c>{{ url | absolute_url }}</c>: <c>site.url</c> followed by the url (a leading slash added); a url with a scheme
    /// is returned as is, and without <c>site.url</c> the url is <c>relative_url</c>'s.
    /// </summary>
    public static async ValueTask<FluidValue> AbsoluteUrl(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        var url = input.ToStringValue(context);

        if (IsAbsolute(url))
        {
            return StringValue.Create(url);
        }

        var path = Rooted(url);

        return await SiteUrlAsync(context) is { } site
            ? StringValue.Create(site.TrimEnd('/') + path)
            : StringValue.Create(path);
    }

    /// <summary>
    /// <c>{{ url | relative_url }}</c>: the url from the root of the server: the path of <c>site.url</c>
    /// (<c>/blog</c> for <c>https://example.com/blog</c>) followed by the url, with a leading slash.
    /// </summary>
    public static async ValueTask<FluidValue> RelativeUrl(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        var url = input.ToStringValue(context);

        if (IsAbsolute(url))
        {
            return StringValue.Create(url);
        }

        var basePath = await SiteUrlAsync(context) is { } site && Uri.TryCreate(site, UriKind.Absolute, out var uri) ? uri.AbsolutePath.TrimEnd('/') : "";
        return StringValue.Create(basePath + Rooted(url));
    }

    /// <summary>
    /// <c>{{ content | strip_html }}</c>: the text without tags, comments, scripts and styles.
    /// </summary>
    public static ValueTask<FluidValue> StripHtml(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        var html = input.ToStringValue(context);

        if (html.IndexOf('<') < 0)
        {
            return StringValue.Create(html);
        }

        var builder = new StringBuilder(html.Length);
        var i = 0;

        while (i < html.Length)
        {
            var c = html[i];

            if (c != '<')
            {
                builder.Append(c);
                i++;
                continue;
            }

            if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
            {
                var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? html.Length : end + 3;
                continue;
            }

            if (StartsTag(html, i, "script") || StartsTag(html, i, "style"))
            {
                var name = StartsTag(html, i, "script") ? "script" : "style";
                var end = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                var close = end < 0 ? -1 : html.IndexOf('>', end);
                i = close < 0 ? html.Length : close + 1;
                continue;
            }

            var tagEnd = html.IndexOf('>', i + 1);

            if (tagEnd < 0)
            {
                builder.Append(html, i, html.Length - i);
                break;
            }

            i = tagEnd + 1;
        }

        return StringValue.Create(builder.ToString());
    }

    /// <summary>
    /// <c>{{ page.date | date_to_xmlschema }}</c>: <c>2020-01-02T03:04:05+00:00</c>.
    /// </summary>
    public static ValueTask<FluidValue> DateToXmlSchema(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        return input.TryGetDateTimeInput(context, out var date)
            ? StringValue.Create(date.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture))
            : input;
    }

    /// <summary>
    /// <c>{{ page.date | date_to_rfc822 }}</c>: <c>Thu, 02 Jan 2020 03:04:05 +0000</c>.
    /// </summary>
    public static ValueTask<FluidValue> DateToRfc822(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        if (!input.TryGetDateTimeInput(context, out var date))
        {
            return input;
        }

        var offset = date.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var zone = $"{sign}{Math.Abs(offset.Hours):00}{Math.Abs(offset.Minutes):00}";

        return StringValue.Create(date.ToString("ddd, dd MMM yyyy HH:mm:ss ", CultureInfo.InvariantCulture) + zone);
    }

    private static async ValueTask<string?> SiteUrlAsync(TemplateContext context)
    {
        var site = await FilterHelpers.ResolveAsync(context, "site");
        return site.IsNil() ? null : FilterHelpers.AsString(await site.GetValueAsync("url", context), context) is { Length: > 0 } url ? url : null;
    }

    private static bool IsAbsolute(string url) => url.StartsWith("//", StringComparison.Ordinal) || url.Contains("://", StringComparison.Ordinal) || url.StartsWith("mailto:", StringComparison.Ordinal);

    private static string Rooted(string url) => url.StartsWith('/') ? url : "/" + url;

    private static bool StartsTag(string html, int index, string name)
    {
        var end = index + 1 + name.Length;
        return end <= html.Length
            && string.Compare(html, index + 1, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0
            && (end == html.Length || html[end] is '>' or ' ' or '\t' or '\n' or '\r' or '/');
    }
}
