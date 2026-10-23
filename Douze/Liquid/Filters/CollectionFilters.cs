using System.Runtime.CompilerServices;
using Douze.Nodes;
using Douze.Workflow;
using Fluid;
using Fluid.Values;

namespace Douze.Liquid.Filters;

/// <summary>
/// Filters over a collection (<c>collections.posts</c>): a list of <c>{ data }</c> items, found by their
/// <c>data.url</c>, compared normalized (no trailing <c>index.html</c>).
/// </summary>
/// <remarks>
/// A collection from the scopes is indexed once by url (published nodes never change), so that finding a page is not a
/// loop over the collection. The render depends on the page's own item (its position) and on the neighbor it returns,
/// not on the whole collection: adding a post elsewhere does not render the page again. A page that is not in the
/// collection depends on all of it. Any other array (the result of a filter) is searched in order.
/// </remarks>
internal static class CollectionFilters
{
    private static readonly ConditionalWeakTable<NodeArray, Dictionary<string, int>> Indexes = new();

    public static void Register(TemplateOptionsBuilder builder)
    {
        builder
            .AddFilter("previous_item", Previous)
            .AddFilter("next_item", Next);
    }

    /// <summary>
    /// The index built for a collection node, if any (tests).
    /// </summary>
    internal static object? IndexOf(NodeArray collection) => Indexes.TryGetValue(collection, out var index) ? index : null;

    /// <summary>
    /// <c>{{ collections.posts | previous_item: url }}</c>: the item before the one whose <c>data.url</c> is the url
    /// (default: the page's <c>url</c>); nil for the first item or a url not in the collection.
    /// </summary>
    public static ValueTask<FluidValue> Previous(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        return NeighborAsync(input, arguments, context, -1);
    }

    /// <summary>
    /// <c>{{ collections.posts | next_item: url }}</c>: the item after the one whose <c>data.url</c> is the url
    /// (default: the page's <c>url</c>); nil for the last item or a url not in the collection.
    /// </summary>
    public static ValueTask<FluidValue> Next(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        return NeighborAsync(input, arguments, context, 1);
    }

    private static async ValueTask<FluidValue> NeighborAsync(FluidValue input, FilterArguments arguments, TemplateContext context, int offset)
    {
        var url = FilterHelpers.AsString(arguments.At(0), context)
            ?? FilterHelpers.AsString(await FilterHelpers.ResolveAsync(context, "url"), context);

        if (url == null || input.Type != FluidValues.Array)
        {
            return NilValue.Instance;
        }

        url = LocaleSet.NormalizeUrl(url);

        if (input is not NodeArrayValue array)
        {
            return await SearchAsync(input, url, offset, context);
        }

        var index = Indexes.GetValue(array.Value, BuildIndex);

        if (!index.TryGetValue(url, out var i))
        {
            array.ReadAll();
            return NilValue.Instance;
        }

        // The page's own item: the result changes when the page moves in the collection.
        await FilterHelpers.GetPathAsync(array[i], "data.url", context);

        var neighbor = i + offset;

        if (neighbor < 0)
        {
            return NilValue.Instance;
        }

        // Past the end reads the missing item: the result changes when one is added.
        return await array.GetIndexAsync(NumberValue.Create(neighbor), context);
    }

    private static Dictionary<string, int> BuildIndex(NodeArray collection)
    {
        var index = new Dictionary<string, int>(collection.Count, StringComparer.Ordinal);

        for (var i = 0; i < collection.Count; i++)
        {
            if (collection[i] is NodeObject item
                && item["data"] is NodeValue<Metadata> data
                && data.Value.Own["url"] is NodeValue<string> { Value: { Length: > 0 } url })
            {
                index.TryAdd(LocaleSet.NormalizeUrl(url), i);
            }
        }

        return index;
    }

    private static async ValueTask<FluidValue> SearchAsync(FluidValue input, string url, int offset, TemplateContext context)
    {
        var items = new List<FluidValue>();

        await foreach (var item in input.EnumerateAsync(context))
        {
            items.Add(item);
        }

        for (var i = 0; i < items.Count; i++)
        {
            if (FilterHelpers.AsString(await FilterHelpers.GetPathAsync(items[i], "data.url", context), context) is { } candidate
                && LocaleSet.NormalizeUrl(candidate) == url)
            {
                var neighbor = i + offset;
                return neighbor >= 0 && neighbor < items.Count ? items[neighbor] : NilValue.Instance;
            }
        }

        return NilValue.Instance;
    }
}
