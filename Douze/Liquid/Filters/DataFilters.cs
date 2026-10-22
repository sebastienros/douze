using Douze.Nodes;
using Fluid;
using Fluid.Filters;
using Fluid.Values;

namespace Douze.Liquid.Filters;

/// <summary>
/// Filters over lists: <c>where_exp</c>, <c>group_by_exp</c>, <c>sort_by</c>, <c>index_by</c>, <c>jsonify</c>, the
/// <c>json</c> filter, and Fluid's <c>where</c>, <c>find_index</c> and <c>slice</c> with fast paths for data arrays.
/// </summary>
internal static class DataFilters
{
    public static void Register(TemplateOptionsBuilder builder)
    {
        builder
            .AddFilter("where_exp", WhereExp)
            .AddFilter("group_by_exp", GroupByExp)
            .AddFilter("sort_by", SortBy)
            .AddFilter("index_by", IndexBy)
            .AddFilter("where", Where)
            .AddFilter("find_index", FindIndex)
            .AddFilter("slice", Slice)
            .AddFilter("json", Json)
            .AddFilter("jsonify", Json);
    }

    /// <summary>
    /// <c>{{ list | where_exp: "item", "item.data.draft != true" }}</c>: the items for which the condition is true.
    /// </summary>
    public static async ValueTask<FluidValue> WhereExp(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        var (variable, expression) = ExpressionArguments("where_exp", arguments, context);
        var result = new List<FluidValue>();

        await foreach (var item in input.EnumerateAsync(context))
        {
            if (await FilterHelpers.EvaluateAsync(context, variable, item, expression, condition: true) == "1")
            {
                result.Add(item);
            }
        }

        return ArrayValue.Create(result);
    }

    /// <summary>
    /// <c>{{ list | group_by_exp: "item", "item.data.date | date: '%Y'" }}</c>: <c>{ name, items, size }</c> per value of
    /// the expression, in the order the values first appear.
    /// </summary>
    public static async ValueTask<FluidValue> GroupByExp(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        var (variable, expression) = ExpressionArguments("group_by_exp", arguments, context);
        var groups = new List<(string Name, List<FluidValue> Items)>();
        var byName = new Dictionary<string, List<FluidValue>>(StringComparer.Ordinal);

        await foreach (var item in input.EnumerateAsync(context))
        {
            var name = await FilterHelpers.EvaluateAsync(context, variable, item, expression, condition: false);

            if (!byName.TryGetValue(name, out var items))
            {
                byName[name] = items = [];
                groups.Add((name, items));
            }

            items.Add(item);
        }

        return ArrayValue.Create([.. groups.Select(x => Object(new Dictionary<string, FluidValue>
        {
            ["name"] = StringValue.Create(x.Name),
            ["items"] = ArrayValue.Create(x.Items),
            ["size"] = NumberValue.Create(x.Items.Count),
        }))]);
    }

    /// <summary>
    /// <c>{{ list | sort_by: "data.title" }}</c>, <c>sort_by: "data.date", "desc"</c>: the items sorted by the value at a
    /// dotted path, numbers and dates by value, strings ordinally, missing values last; a stable sort.
    /// </summary>
    public static async ValueTask<FluidValue> SortBy(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        var path = FilterHelpers.AsString(arguments.At(0), context) ?? throw new DouzeException("sort_by needs the path of the value to sort by");
        var descending = FilterHelpers.AsString(arguments.At(1), context) is "desc" or "descending";
        var keyed = new List<(FluidValue Key, FluidValue Item)>();

        await foreach (var item in input.EnumerateAsync(context))
        {
            keyed.Add((await FilterHelpers.GetPathAsync(item, path, context), item));
        }

        var comparer = Comparer<FluidValue>.Create((a, b) => Compare(a, b, context));
        var sorted = descending ? keyed.OrderByDescending(x => x.Key, comparer) : keyed.OrderBy(x => x.Key, comparer);

        // Missing values stay last in both orders.
        return ArrayValue.Create([.. sorted.Where(x => !IsMissing(x.Key)).Concat(keyed.Where(x => IsMissing(x.Key))).Select(x => x.Item)]);
    }

    /// <summary>
    /// <c>{{ list | index_by: "data.url" }}</c>: an object of the items by the value at a dotted path; the first item of a
    /// value wins, items without one are left out.
    /// </summary>
    public static async ValueTask<FluidValue> IndexBy(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        var path = FilterHelpers.AsString(arguments.At(0), context) ?? throw new DouzeException("index_by needs the path of the key");
        var result = new Dictionary<string, FluidValue>(StringComparer.Ordinal);

        await foreach (var item in input.EnumerateAsync(context))
        {
            if (FilterHelpers.AsString(await FilterHelpers.GetPathAsync(item, path, context), context) is { } key)
            {
                result.TryAdd(key, item);
            }
        }

        return Object(result);
    }

    /// <summary>
    /// <c>{{ list | where: "module", name }}</c>: Fluid's filter, answered from a <see cref="MemberIndex"/> when the list
    /// is a large data array (or a slice of one) and the value a string.
    /// </summary>
    public static ValueTask<FluidValue> Where(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        return MemberIndex.TryGet(input, arguments, context, out var array, out var lookup, out var value)
            ? new(lookup.Where(array, value))
            : ArrayFilters.Where(input, arguments, context);
    }

    /// <summary>
    /// <c>{{ list | find_index: "file", name }}</c>: Fluid's filter, answered from a <see cref="MemberIndex"/> when the
    /// list is a large data array (or a slice of one) and the value a string.
    /// </summary>
    public static ValueTask<FluidValue> FindIndex(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        if (!MemberIndex.TryGet(input, arguments, context, out _, out var lookup, out var value))
        {
            return ArrayFilters.FindIndex(input, arguments, context);
        }

        return new(lookup.FindIndex(value) is { } found ? NumberValue.Create(found) : NilValue.Instance);
    }

    /// <summary>
    /// <c>{{ list | slice: 10, 20 }}</c>: Fluid's filter; a slice of a data array is a data array of its own
    /// (<see cref="NodeArrayValue.Slice"/>), whose items are read as the items of the array, so that <c>where</c> and
    /// <c>find_index</c> over it use the index of the array.
    /// </summary>
    public static ValueTask<FluidValue> Slice(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        if (input is not NodeArrayValue array || arguments.Count is not (1 or 2) || !arguments.At(0).IsInteger()
            || arguments.Count == 2 && !arguments.At(1).IsNil() && !arguments.At(1).IsInteger())
        {
            return StringFilters.Slice(input, arguments, context);
        }

        // Fluid's bounds: a negative start counts from the end, the length defaults to 1.
        var requested = Convert.ToInt32(arguments.At(0).ToNumberValue(context));
        var length = arguments.Count == 2 && !arguments.At(1).IsNil() ? Convert.ToInt32(arguments.At(1).ToNumberValue(context)) : 1;

        if (length <= 0)
        {
            return new(ArrayValue.Empty);
        }

        var count = array.Count;

        if (requested < -count)
        {
            return new(ArrayValue.Empty);
        }

        var start = requested < 0 ? Math.Max(count + requested, 0) : Math.Min(requested, count);
        length = Math.Min(length, count - start);

        if (length <= 0)
        {
            return new(ArrayValue.Empty);
        }

        context.EnsureCollectionSize(length);
        return new(array.Slice(start, length));
    }

    private static (string Variable, string Expression) ExpressionArguments(string filter, FilterArguments arguments, TemplateContext context)
    {
        if (FilterHelpers.AsString(arguments.At(0), context) is not { Length: > 0 } variable || FilterHelpers.AsString(arguments.At(1), context) is not { Length: > 0 } expression)
        {
            throw new DouzeException($"{filter} needs a variable name and an expression: {filter}: \"item\", \"item.data.draft != true\"");
        }

        return (variable, expression);
    }

    private static FluidValue Object(Dictionary<string, FluidValue> values) => DictionaryValue.Create(new FluidValueDictionaryFluidIndexable(values));

    private static bool IsMissing(FluidValue value) => value.IsNil() || value is UndefinedValue;

    private static int Compare(FluidValue a, FluidValue b, TemplateContext context)
    {
        if (IsMissing(a) || IsMissing(b))
        {
            return IsMissing(a).CompareTo(IsMissing(b));
        }

        if (a.Type == FluidValues.Number && b.Type == FluidValues.Number)
        {
            return a.ToNumberValue(context).CompareTo(b.ToNumberValue(context));
        }

        if (a.Type == FluidValues.DateTime && b.Type == FluidValues.DateTime && a.TryGetDateTimeInput(context, out var x) && b.TryGetDateTimeInput(context, out var y))
        {
            return x.CompareTo(y);
        }

        return string.CompareOrdinal(a.ToStringValue(context), b.ToStringValue(context));
    }

    /// <summary>
    /// <c>json</c> and <c>jsonify</c>: a data object or array (site data, a collection) as JSON, in its own order;
    /// other values through Fluid's <c>json</c> filter. Fluid's filter alone writes <c>null</c> for nodes, which its
    /// source generated serializer does not know.
    /// </summary>
    public static ValueTask<FluidValue> Json(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        // ToObjectValue records the whole value as read (the page depends on all of it).
        Node? node = input is NodeObjectValue or NodeArrayValue ? input.ToObjectValue(context) as Node : null;

        if (node == null)
        {
            return MiscFilters.Json(input, arguments, context);
        }

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();

        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            NodeJson.Write(writer, node);
        }

        return new ValueTask<FluidValue>(StringValue.Create(System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan), encode: false));
    }
}
