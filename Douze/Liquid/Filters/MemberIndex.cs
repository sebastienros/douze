using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Douze.Nodes;
using Fluid;
using Fluid.Values;

namespace Douze.Liquid.Filters;

/// <summary>
/// The items of a data array by the string value of one of their members, built once per array and member, so that
/// <c>where</c> and <c>find_index</c> with a string over a large array of objects (a route list looked up by every page), or over a
/// slice of one (<see cref="NodeArrayValue.Slice"/>), do not scan it on each call. Data nodes are never changed once
/// published, so an index stays valid as long as its array lives.
/// </summary>
/// <remarks>
/// The answers are Fluid's: an object item matches when its member is that string; a nil item makes the filter nil
/// once it is reached (<c>where</c> reaches every item, <c>find_index</c> those before its match). An array with any
/// other kind of item has no index: Fluid reads a property of a string or an integer like Ruby's <c>[]</c> (a string
/// item matches when it contains the member and the value is the member), flattens a nested array, and stops at a
/// boolean or a float.
/// </remarks>
internal sealed class MemberIndex
{
    /// <summary>
    /// The smallest array indexed: scanning a smaller one costs about what building its index does.
    /// </summary>
    public const int MinimumCount = 32;

    private static readonly ConditionalWeakTable<NodeArray, ConcurrentDictionary<string, MemberIndex?>> Indexes = new();

    private readonly Dictionary<string, int[]> _objects;
    private readonly int[] _nils;

    private MemberIndex(Dictionary<string, int[]> objects, int[] nils)
    {
        _objects = objects;
        _nils = nils;
    }

    /// <summary>
    /// The index of a filter's input by its first argument, when the input is a large data array or a slice of one,
    /// the member a string and the value (second argument) a string that is not empty. The input is then read whole
    /// (the result depends on every item); <paramref name="array"/> is its items, <paramref name="lookup"/> answers in
    /// their indices.
    /// </summary>
    public static bool TryGet(FluidValue input, FilterArguments arguments, TemplateContext context, out NodeArray array, out Lookup lookup, out string value)
    {
        array = null!;
        lookup = default;
        value = null!;

        if (input is not NodeArrayValue arrayValue
            || (arrayValue.Source ?? arrayValue.Value) is not { Count: >= MinimumCount } source
            || arguments.Count != 2
            || arguments.At(0) is not StringValue member
            || arguments.At(1) is not StringValue target
            || target.ToStringValue(context) is not { Length: > 0 } text)
        {
            return false;
        }

        var index = Indexes.GetOrCreateValue(source).GetOrAdd(member.ToStringValue(context), static (name, state) => Build(state.Source, name, state.Context), (Source: source, Context: context));

        if (index == null)
        {
            return false;
        }

        array = arrayValue.ReadAll();
        lookup = new Lookup(index, arrayValue.Source == null ? 0 : arrayValue.Offset, array.Count);
        value = text;
        return true;
    }

    /// <summary>
    /// The answers of an index for the items of its array from <c>start</c>, <c>count</c> of them (a slice), in the
    /// indices of the slice.
    /// </summary>
    internal readonly struct Lookup(MemberIndex index, int start, int count)
    {
        /// <summary>
        /// The object items of <paramref name="items"/> (the slice) whose member is <paramref name="value"/>, in order:
        /// <c>where</c>; nil when the slice has a nil item (Fluid stops there).
        /// </summary>
        public FluidValue Where(NodeArray items, string value)
        {
            if (!Range(index._nils).IsEmpty)
            {
                return NilValue.Instance;
            }

            var matches = index._objects.TryGetValue(value, out var all) ? Range(all) : [];

            if (matches.IsEmpty)
            {
                return ArrayValue.Empty;
            }

            var result = new FluidValue[matches.Length];

            for (var i = 0; i < result.Length; i++)
            {
                result[i] = NodeValues.ToFluidValue(items[matches[i] - start]);
            }

            return ArrayValue.Create(result);
        }

        /// <summary>
        /// The first item that matches <paramref name="value"/>: <c>find_index</c>; null when there is none, or when a
        /// nil item comes before it (Fluid stops there).
        /// </summary>
        public int? FindIndex(string value)
        {
            var first = index._objects.TryGetValue(value, out var items) ? First(items) : int.MaxValue;
            return first == int.MaxValue || First(index._nils) < first ? null : first - start;
        }

        private int First(int[] items) => Range(items) is { IsEmpty: false } range ? range[0] : int.MaxValue;

        private ReadOnlySpan<int> Range(int[] items)
        {
            if (start == 0 && (items.Length == 0 || items[^1] < count))
            {
                return items;
            }

            var from = LowerBound(items, start);
            var to = LowerBound(items, start + count);
            return items.AsSpan(from, to - from);
        }

        private static int LowerBound(int[] items, int value)
        {
            var found = Array.BinarySearch(items, value);
            return found >= 0 ? found : ~found;
        }
    }

    private static MemberIndex? Build(NodeArray array, string member, TemplateContext context)
    {
        var objects = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var nils = new List<int>();

        for (var i = 0; i < array.Count; i++)
        {
            switch (array[i])
            {
                case null:
                    nils.Add(i);
                    break;

                case NodeObject item:
                    // A missing member is nil, a member of another type is not a string: neither matches a string.
                    if (item.TryGetItem(member, out var node) && NodeValues.ToFluidValue(node) is StringValue memberValue)
                    {
                        Add(objects, memberValue.ToStringValue(context), i);
                    }

                    break;

                default:
                    return null;
            }
        }

        return new MemberIndex(ToArrays(objects), [.. nils]);

        static void Add(Dictionary<string, List<int>> map, string key, int index)
        {
            if (!map.TryGetValue(key, out var indices))
            {
                map[key] = indices = [];
            }

            indices.Add(index);
        }

        static Dictionary<string, int[]> ToArrays(Dictionary<string, List<int>> map) => map.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.Ordinal);
    }
}
