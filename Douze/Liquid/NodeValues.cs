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
        return other is NodeObjectValue nodeObject && (ReferenceEquals(nodeObject.Value, Value) || nodeObject.Value.Equals(Value));
    }

    // An object is true, 0 and empty whatever its members.
    [Obsolete("Use the TemplateContext overload.")]
    public override bool ToBooleanValue() => true;

    [Obsolete("Use the TemplateContext overload.")]
    public override decimal ToNumberValue() => 0;

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

    public NodeArrayValue(NodeArray value, AccessRecorder? recorder = null)
    {
        Value = value;
        _recorder = recorder;
    }

    public NodeArray Value { get; }

    public override FluidValues Type => FluidValues.Array;

    public int Count
    {
        get
        {
            _recorder?.ReadCount();
            return Value.Count;
        }
    }

    public FluidValue this[int index] => NodeValues.ToFluidValue(Value[index], _recorder?.Item(index));

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
                _recorder?.Item(0);
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
        _recorder?.Item(i);
        return new(NilValue.Instance);
    }

    public override async ValueTask<bool> ContainsAsync(FluidValue value, TemplateContext context)
    {
        _recorder?.ReadWhole();

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
        _recorder?.ReadWhole();

        foreach (var item in Value)
        {
            yield return NodeValues.ToFluidValue(item);
        }

        await Task.CompletedTask;
    }

    // Equal to any IFluidArray with equal items, like Fluid's ArrayValue, so that comparisons agree in both directions.
    public override bool Equals(FluidValue other)
    {
        _recorder?.ReadWhole();

        if (other is NodeArrayValue nodeArray && (ReferenceEquals(nodeArray.Value, Value) || nodeArray.Value.Equals(Value)))
        {
            return true;
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

    // An array is true and 0 whatever its items.
    [Obsolete("Use the TemplateContext overload.")]
    public override bool ToBooleanValue() => true;

    [Obsolete("Use the TemplateContext overload.")]
    public override decimal ToNumberValue() => 0;

    [Obsolete("Use the TemplateContext overload.")]
    public override string ToStringValue()
    {
        _recorder?.ReadWhole();
        return string.Concat(Value.Select(x => NodeValues.ToFluidValue(x).ToStringValue()));
    }

    [Obsolete("Use the TemplateContext overload.")]
    public override object ToObjectValue()
    {
        _recorder?.ReadWhole();
        return Value;
    }

    public override async ValueTask WriteToAsync(IFluidOutput output, TextEncoder encoder, CultureInfo cultureInfo)
    {
        _recorder?.ReadWhole();

        foreach (var item in Value)
        {
            await NodeValues.ToFluidValue(item).WriteToAsync(output, encoder, cultureInfo);
        }
    }

    public override bool Equals(object? obj) => obj is FluidValue other && Equals(other);

    // Hashes the items like Fluid's ArrayValue, so that equal arrays of both kinds have equal hashes.
    public override int GetHashCode()
    {
        _recorder?.ReadWhole();

        var hash = new HashCode();

        foreach (var item in Value)
        {
            hash.Add(NodeValues.ToFluidValue(item));
        }

        return hash.ToHashCode();
    }
}
