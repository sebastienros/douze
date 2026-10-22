using System.Globalization;
using System.Text.Encodings.Web;
using Douze.Nodes;
using Douze.Workflow;
using Fluid;
using Fluid.Values;

namespace Douze.Liquid;

/// <summary>
/// Explicit, reflection free conversions from nodes to Fluid values.
/// </summary>
internal static class NodeValues
{
    /// <summary>
    /// A Fluid value converter: returns the <see cref="FluidValue"/> for nodes and metadata, <c>null</c> for anything else.
    /// </summary>
    public static object? Convert(object value)
    {
        return value switch
        {
            Node node => ToFluidValue(node),
            Metadata metadata => new MetadataValue(metadata),
            _ => null,
        };
    }

    /// <summary>
    /// Converts a node read from the scopes, recording what the template reads of it in <paramref name="recorder"/>:
    /// the members and items of objects and arrays, the whole of anything else. Without a recorder, converts the node.
    /// </summary>
    public static FluidValue ToFluidValue(Node? node, AccessRecorder? recorder)
    {
        switch (node)
        {
            case null when recorder != null:
                recorder.ReadWhole();
                return NilValue.Instance;
            case NodeObject obj when recorder != null:
                return new NodeObjectValue(obj, recorder);
            case NodeArray array when recorder != null:
                return new NodeArrayValue(array, recorder);
        }

        recorder?.ReadWhole();
        return ToFluidValue(node);
    }

    public static FluidValue ToFluidValue(Node? node)
    {
        return node switch
        {
            null => NilValue.Instance,
            NodeObject obj => new NodeObjectValue(obj),
            NodeArray array => new NodeArrayValue(array),
            NodeValue<string> s => StringValue.Create(s.Value),
            NodeValue<bool> b => BooleanValue.Create(b.Value),
            NodeValue<int> i => NumberValue.Create(i.Value),
            NodeValue<long> l => NumberValue.Create(l.Value),
            NodeValue<decimal> m => NumberValue.Create(m.Value),
            NodeValue<double> d => NumberValue.Create((decimal)d.Value),
            NodeValue<float> f => NumberValue.Create((decimal)f.Value),
            NodeValue<short> sh => NumberValue.Create(sh.Value),
            NodeValue<byte> by => NumberValue.Create(by.Value),
            NodeValue<uint> ui => NumberValue.Create(ui.Value),
            NodeValue<ulong> ul => NumberValue.Create(ul.Value),
            NodeValue<DateTime> dt => DateTimeValue.Create(dt.Value),
            NodeValue<DateTimeOffset> dto => DateTimeValue.Create(dto.Value),
            NodeValue<Metadata> meta => new MetadataValue(meta.Value),
            IFormattable formattable => StringValue.Create(formattable.ToString(null, CultureInfo.InvariantCulture)),
            _ => StringValue.Create(node.ToString() ?? ""),
        };
    }
}

/// <summary>
/// A <see cref="NodeObject"/> seen from a template, like Fluid's dictionary value. With a recorder (a value from the
/// scopes), records the members the template reads (<see cref="AccessRecorder"/>), and the whole object when it is
/// enumerated, compared or converted.
/// </summary>
internal sealed class NodeObjectValue : FluidValue
{
    private readonly AccessRecorder? _recorder;

    public NodeObjectValue(NodeObject value, AccessRecorder? recorder = null)
    {
        Value = value;
        _recorder = recorder;
    }

    public NodeObject Value { get; }

    /// <summary>
    /// The object, read whole: for a filter that uses all of it.
    /// </summary>
    internal NodeObject ReadAll()
    {
        _recorder?.ReadWhole();
        return Value;
    }

    public override FluidValues Type => FluidValues.Dictionary;

    public override ValueTask<FluidValue> GetValueAsync(string name, TemplateContext context)
    {
        // A member that does not exist is read too: the result changes when it is added.
        var member = _recorder?.Member(name);

        if (Value.TryGetItem(name, out var node))
        {
            return new(NodeValues.ToFluidValue(node, member));
        }

        if (name == "size")
        {
            _recorder?.ReadCount();
            return new(NumberValue.Create(Value.Count));
        }

        return new(NilValue.Instance);
    }

    public override ValueTask<FluidValue> GetIndexAsync(FluidValue index, TemplateContext context)
    {
        return GetValueAsync(index.ToStringValue(context), context);
    }

    [Obsolete("Use the TemplateContext overload.")]
    public override bool Contains(FluidValue value)
    {
        _recorder?.ReadWhole();
        return Value.ContainsKey(value.ToStringValue());
    }

    public override ValueTask<bool> ContainsAsync(FluidValue value, TemplateContext context)
    {
        _recorder?.ReadWhole();
        return new(Value.ContainsKey(value.ToStringValue(context)));
    }

    public override async IAsyncEnumerable<FluidValue> EnumerateAsync(TemplateContext context)
    {
        _recorder?.ReadWhole();

        foreach (var property in Value)
        {
            yield return ArrayValue.Create([StringValue.Create(property.Key), NodeValues.ToFluidValue(property.Value)]);
        }

        await Task.CompletedTask;
    }

    public override bool Equals(FluidValue other)
    {
        _recorder?.ReadWhole();

        if (other is not NodeObjectValue nodeObject)
        {
            return false;
        }

        // The other object is read whole too: it may be a value from the scopes of its own.
        nodeObject._recorder?.ReadWhole();
        return ReferenceEquals(nodeObject.Value, Value) || nodeObject.Value.Equals(Value);
    }

    // An object is true whatever its members. Like Fluid's dictionary value, it counts its members: 'empty' and the
    // 'default' filter compare that number to 0.
    [Obsolete("Use the TemplateContext overload.")]
    public override bool ToBooleanValue() => true;

    [Obsolete("Use the TemplateContext overload.")]
    public override decimal ToNumberValue()
    {
        _recorder?.ReadCount();
        return Value.Count;
    }

    [Obsolete("Use the TemplateContext overload.")]
    public override string ToStringValue() => "";

    [Obsolete("Use the TemplateContext overload.")]
    public override object ToObjectValue()
    {
        _recorder?.ReadWhole();
        return Value;
    }

    public override ValueTask WriteToAsync(IFluidOutput output, TextEncoder encoder, CultureInfo cultureInfo) => default;

    public override bool Equals(object? obj) => obj is FluidValue other && Equals(other);

    public override int GetHashCode()
    {
        _recorder?.ReadWhole();
        return Value.GetHashCode();
    }
}

/// <summary>
/// A <see cref="NodeArray"/> seen from a template, like Fluid's array value. Items are converted when they are read:
/// through <see cref="IFluidArray"/>, <c>for ... limit:</c> and the <c>first</c>, <c>last</c> and <c>slice</c> filters
/// convert only the items they use. With a recorder (a value from the scopes), records the items the template reads and
/// whether it read their number (<see cref="AccessRecorder"/>), and the whole array when it is enumerated, compared or
/// written.
/// </summary>
internal sealed class NodeArrayValue : FluidValue, IFluidArray
{
    private readonly AccessRecorder? _recorder;

    // A slice (the slice filter): the items of Source from Offset, whose reads are recorded as reads of Source's items.
    private readonly int _offset;

    public NodeArrayValue(NodeArray value, AccessRecorder? recorder = null)
    {
        Value = value;
        _recorder = recorder;
    }

    private NodeArrayValue(NodeArray value, AccessRecorder? recorder, NodeArray source, int offset)
    {
        Value = value;
        _recorder = recorder;
        Source = source;
        _offset = offset;
    }

    public NodeArray Value { get; }

    /// <summary>
    /// The array this one is a slice of, or null.
    /// </summary>
    public NodeArray? Source { get; }

    /// <summary>
    /// The index in <see cref="Source"/> of the first item of a slice.
    /// </summary>
    public int Offset => _offset;

    /// <summary>
    /// <paramref name="length"/> items from <paramref name="start"/> (both within the array), with the recorder of this
    /// array: reading an item of the slice is reading the item of this array, and reading the slice whole or its size
    /// is reading this array whole or its size.
    /// </summary>
    internal NodeArrayValue Slice(int start, int length)
    {
        var items = new Node?[length];

        for (var i = 0; i < length; i++)
        {
            items[i] = Value[start + i];
        }

        return new NodeArrayValue(new NodeArray(items), _recorder, Source ?? Value, _offset + start);
    }

    /// <summary>
    /// The array, read whole: for a filter that uses all of it.
    /// </summary>
    internal NodeArray ReadAll()
    {
        RecordWhole();
        return Value;
    }

    /// <summary>
    /// Records the array as read whole; for a slice, its items (each whole) in the array it is a slice of.
    /// </summary>
    private void RecordWhole()
    {
        if (_recorder == null)
        {
            return;
        }

        // More items than the recorder keeps is the array read whole anyway.
        if (Source == null || Value.Count > AccessRecorder.MaxEntries)
        {
            _recorder.ReadWhole();
            return;
        }

        for (var i = 0; i < Value.Count; i++)
        {
            _recorder.Item(_offset + i).ReadWhole();
        }
    }

    public override FluidValues Type => FluidValues.Array;

    public int Count
    {
        get
        {
            _recorder?.ReadCount();
            return Value.Count;
        }
    }

    public FluidValue this[int index] => NodeValues.ToFluidValue(Value[index], _recorder?.Item(_offset + index));

    public override ValueTask<FluidValue> GetValueAsync(string name, TemplateContext context)
    {
        var count = Value.Count;

        switch (name)
        {
            case "size":
                _recorder?.ReadCount();
                return new(NumberValue.Create(count));

            case "first":
                // A missing first item is read as such: no need for the count.
                _recorder?.Item(_offset);
                return new(count > 0 ? this[0] : NilValue.Instance);

            case "last":
                _recorder?.ReadCount();
                return new(count > 0 ? this[count - 1] : NilValue.Instance);

            default:
                return new(NilValue.Instance);
        }
    }

    public override ValueTask<FluidValue> GetIndexAsync(FluidValue index, TemplateContext context)
    {
        var i = (int)index.ToNumberValue(context);
        var count = Value.Count;

        if (i < 0)
        {
            _recorder?.ReadCount();
            i += count;
        }

        if (i >= 0 && i < count)
        {
            return new(this[i]);
        }

        // An item that does not exist is read too: the result changes when it is added.
        _recorder?.Item(_offset + i);
        return new(NilValue.Instance);
    }

    public override async ValueTask<bool> ContainsAsync(FluidValue value, TemplateContext context)
    {
        RecordWhole();

        foreach (var item in Value)
        {
            if (NodeValues.ToFluidValue(item).Equals(value))
            {
                return true;
            }
        }

        await Task.CompletedTask;
        return false;
    }

    public override async IAsyncEnumerable<FluidValue> EnumerateAsync(TemplateContext context)
    {
        RecordWhole();

        foreach (var item in Value)
        {
            yield return NodeValues.ToFluidValue(item);
        }

        await Task.CompletedTask;
    }

    // Equal to any IFluidArray with equal items, like Fluid's ArrayValue, so that comparisons agree in both directions.
    public override bool Equals(FluidValue other)
    {
        RecordWhole();

        if (other is NodeArrayValue nodeArray)
        {
            // The other array is read whole too: it may be a value from the scopes of its own.
            nodeArray.RecordWhole();

            if (ReferenceEquals(nodeArray.Value, Value) || nodeArray.Value.Equals(Value))
            {
                return true;
            }
        }

        if (other is IFluidArray array && other.Type == FluidValues.Array)
        {
            if (array.Count != Value.Count)
            {
                return false;
            }

            for (var i = 0; i < Value.Count; i++)
            {
                if (!NodeValues.ToFluidValue(Value[i]).Equals(array[i] ?? NilValue.Instance))
                {
                    return false;
                }
            }

            return true;
        }

        return (other.IsNil() || other.Type == FluidValues.Empty) && Value.Count == 0;
    }

    // An array is true whatever its items. Like Fluid's array value, it counts its items: 'empty' and the 'default'
    // filter compare that number to 0.
    [Obsolete("Use the TemplateContext overload.")]
    public override bool ToBooleanValue() => true;

    [Obsolete("Use the TemplateContext overload.")]
    public override decimal ToNumberValue()
    {
        _recorder?.ReadCount();
        return Value.Count;
    }

    [Obsolete("Use the TemplateContext overload.")]
    public override string ToStringValue()
    {
        RecordWhole();
        return string.Concat(Value.Select(x => NodeValues.ToFluidValue(x).ToStringValue()));
    }

    [Obsolete("Use the TemplateContext overload.")]
    public override object ToObjectValue()
    {
        RecordWhole();
        return Value;
    }

    public override async ValueTask WriteToAsync(IFluidOutput output, TextEncoder encoder, CultureInfo cultureInfo)
    {
        RecordWhole();

        foreach (var item in Value)
        {
            await NodeValues.ToFluidValue(item).WriteToAsync(output, encoder, cultureInfo);
        }
    }

    public override bool Equals(object? obj) => obj is FluidValue other && Equals(other);

    // Hashes the items like Fluid's ArrayValue, so that equal arrays of both kinds have equal hashes.
    public override int GetHashCode()
    {
        RecordWhole();

        var hash = new HashCode();

        foreach (var item in Value)
        {
            hash.Add(NodeValues.ToFluidValue(item));
        }

        return hash.ToHashCode();
    }
}
