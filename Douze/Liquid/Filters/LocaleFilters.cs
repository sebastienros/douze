using Douze.Nodes;
using Douze.Workflow;
using Fluid;
using Fluid.Values;

namespace Douze.Liquid.Filters;

/// <summary>
/// The filters of localized sites (<c>site.locales</c>): <c>t</c>, <c>locale_url</c>, <c>locale_of</c> and
/// <c>locale_pages</c>.
/// </summary>
internal static class LocaleFilters
{
    public static void Register(TemplateOptionsBuilder builder)
    {
        builder
            .AddFilter("t", Translate)
            .AddFilter("locale_url", LocaleUrl)
            .AddFilter("locale_of", LocaleOf)
            .AddFilter("locale_pages", LocalePages);
    }

    /// <summary>
    /// <c>{{ "search.label" | t }}</c>: the UI string at that key in the strings of the page's <c>locale</c>
    /// (<c>i18n.&lt;locale&gt;</c>, read through the model so that the page depends on that string only), else of the
    /// default locale, else the key itself, with a warning once per key. A key is a member (<c>"search.label"</c>) or a
    /// path of nested members (<c>search</c> then <c>label</c>). Named arguments replace placeholders:
    /// <c>{{ "pages.count" | t: count: 3 }}</c> replaces <c>{count}</c>.
    /// </summary>
    public static async ValueTask<FluidValue> Translate(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        var key = input.ToStringValue(context);
        var locales = await FilterHelpers.GetLocalesAsync(context);
        var locale = FilterHelpers.AsString(await FilterHelpers.ResolveAsync(context, "locale"), context) ?? locales.Default.Key;
        var strings = await FilterHelpers.ResolveAsync(context, locales.Strings);

        var found = await LookupAsync(strings, locale, key, context);

        if (found == null && locale != locales.Default.Key)
        {
            found = await LookupAsync(strings, locales.Default.Key, key, context);
        }

        if (found == null)
        {
            FilterHelpers.WarnOnce(context, $"no translation for '{key}' in '{locales.Strings}.{locale}'");
            return StringValue.Create(key);
        }

        foreach (var name in arguments.Names)
        {
            found = found.Replace("{" + name + "}", arguments[name].ToStringValue(context), StringComparison.Ordinal);
        }

        return StringValue.Create(found);
    }

    private static async ValueTask<string?> LookupAsync(FluidValue strings, string locale, string key, TemplateContext context)
    {
        if (strings.IsNil())
        {
            return null;
        }

        var table = await strings.GetValueAsync(locale, context);

        if (table.IsNil())
        {
            return null;
        }

        var value = await table.GetValueAsync(key, context);

        if (value.IsNil() && key.Contains('.'))
        {
            value = await FilterHelpers.GetPathAsync(table, key, context);
        }

        return value.IsNil() || value.Type is FluidValues.Dictionary or FluidValues.Array ? null : value.ToStringValue(context);
    }

    /// <summary>
    /// <c>{{ url | locale_url: "ja" }}</c>: the url in another locale, whether or not a page exists there: the url
    /// normalized (no <c>index.html</c>), without its locale prefix, with the prefix of the locale (none for the default
    /// locale). Without an argument, the url without its locale prefix.
    /// </summary>
    public static async ValueTask<FluidValue> LocaleUrl(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        var locales = await FilterHelpers.GetLocalesAsync(context);
        var url = locales.StripPrefix(LocaleSet.NormalizeUrl(input.ToStringValue(context)));

        if (FilterHelpers.AsString(arguments.At(0), context) is not { } key)
        {
            return StringValue.Create(url);
        }

        if (!locales.TryGet(key, out var locale))
        {
            throw new DouzeException($"locale_url: unknown locale '{key}'");
        }

        return StringValue.Create(locale.Prefix + url);
    }

    /// <summary>
    /// <c>{{ url | locale_of }}</c>: the key of the locale whose prefix starts the url, the default locale's otherwise.
    /// </summary>
    public static async ValueTask<FluidValue> LocaleOf(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        var locales = await FilterHelpers.GetLocalesAsync(context);
        return StringValue.Create(locales.LocaleOfUrl(input.ToStringValue(context)).Key);
    }

    /// <summary>
    /// <c>{{ collections.all | locale_pages }}</c>: every page the site serves in every locale, as
    /// <c>{ locale, lang, url, fallback, data }</c> (<c>url</c> normalized): each collection item with its locale, and
    /// with <c>site.locales.fallback</c>, for a page of the default locale under the locale root, one entry per locale
    /// that has no page at its url (<c>fallback: true</c>, the data of the default locale's page), as the
    /// <c>localize</c> step copies them. In the order of the items, each followed by its fallbacks.
    /// </summary>
    public static async ValueTask<FluidValue> LocalePages(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        var locales = await FilterHelpers.GetLocalesAsync(context);
        var pages = new List<(Metadata Metadata, string Url, Locale Locale, bool Rooted)>();

        await foreach (var item in input.EnumerateAsync(context))
        {
            if (await item.GetValueAsync("data", context) is not MetadataValue { Metadata: var metadata }
                || metadata.Own["url"] is not NodeValue<string> { Value: { Length: > 0 } url })
            {
                continue;
            }

            var rooted = locales.Contains(metadata.Directory);
            pages.Add((metadata, LocaleSet.NormalizeUrl(url), rooted ? locales.LocaleOfDirectory(metadata.Directory) : locales.Default, rooted));
        }

        var existing = pages.Select(x => x.Url).ToHashSet(StringComparer.Ordinal);
        var result = new NodeArray();

        foreach (var (metadata, url, locale, rooted) in pages)
        {
            result.Add(Entry(locale, url, fallback: false, metadata));

            if (!locales.Fallback || !rooted || !locale.IsDefault)
            {
                continue;
            }

            foreach (var other in locales.Languages)
            {
                if (!existing.Contains(other.Prefix + url))
                {
                    result.Add(Entry(other, other.Prefix + url, fallback: true, metadata));
                }
            }
        }

        return NodeValues.ToFluidValue(result);

        static NodeObject Entry(Locale locale, string url, bool fallback, Metadata metadata) => new()
        {
            ["locale"] = NodeValue.Create(locale.Key),
            ["lang"] = NodeValue.Create(locale.Lang),
            ["url"] = NodeValue.Create(url),
            ["fallback"] = NodeValue.Create(fallback),
            ["data"] = new NodeValue<Metadata>(metadata),
        };
    }
}
