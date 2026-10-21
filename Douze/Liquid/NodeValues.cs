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
/// A <see cref="NodeObject"/> seen from a template, like Fluid's dictionary value.
/// </summary>
internal sealed class NodeObjectValue : FluidValue
{
    public NodeObjectValue(NodeObject value)
    {
        Value = value;
    }

    public NodeObject Value { get; }

    public override FluidValues Type => FluidValues.Dictionary;

    public override ValueTask<FluidValue> GetValueAsync(string name, TemplateContext context)
    {
        if (Value.TryGetItem(name, out var node))
        {
            return new(NodeValues.ToFluidValue(node));
        }

        if (name == "size")
        {
            return new(NumberValue.Create(Value.Count));
        }

        return new(NilValue.Instance);
    }

    public override ValueTask<FluidValue> GetIndexAsync(FluidValue index, TemplateContext context)
    {
        return GetValueAsync(index.ToStringValue(context), context);
    }

    [Obsolete("Use the TemplateContext overload.")]
    public override bool Contains(FluidValue value) => Value.ContainsKey(value.ToStringValue());

    public override ValueTask<bool> ContainsAsync(FluidValue value, TemplateContext context) => new(Value.ContainsKey(value.ToStringValue(context)));

    public override async IAsyncEnumerable<FluidValue> EnumerateAsync(TemplateContext context)
    {
        foreach (var property in Value)
        {
            yield return ArrayValue.Create([StringValue.Create(property.Key), NodeValues.ToFluidValue(property.Value)]);
        }

        await Task.CompletedTask;
    }

    public override bool Equals(FluidValue other)
    {
        return other is NodeObjectValue nodeObject && (ReferenceEquals(nodeObject.Value, Value) || nodeObject.Value.Equals(Value));
    }

    [Obsolete("Use the TemplateContext overload.")]
    public override bool ToBooleanValue() => true;

    [Obsolete("Use the TemplateContext overload.")]
    public override decimal ToNumberValue() => 0;

    [Obsolete("Use the TemplateContext overload.")]
    public override string ToStringValue() => "";

    [Obsolete("Use the TemplateContext overload.")]
    public override object ToObjectValue() => Value;

    public override ValueTask WriteToAsync(IFluidOutput output, TextEncoder encoder, CultureInfo cultureInfo) => default;

    public override bool Equals(object? obj) => obj is FluidValue other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();
}

/// <summary>
/// A <see cref="NodeArray"/> seen from a template, like Fluid's array value.
/// </summary>
internal sealed class NodeArrayValue : FluidValue
{
    public NodeArrayValue(NodeArray value)
    {
        Value = value;
    }

    public NodeArray Value { get; }

    public override FluidValues Type => FluidValues.Array;

    public override ValueTask<FluidValue> GetValueAsync(string name, TemplateContext context)
    {
        var count = Value.Count;

        return name switch
        {
            "size" => new(NumberValue.Create(count)),
            "first" => new(count > 0 ? NodeValues.ToFluidValue(Value[0]) : NilValue.Instance),
            "last" => new(count > 0 ? NodeValues.ToFluidValue(Value[count - 1]) : NilValue.Instance),
            _ => new(NilValue.Instance),
        };
    }

    public override ValueTask<FluidValue> GetIndexAsync(FluidValue index, TemplateContext context)
    {
        var i = (int)index.ToNumberValue(context);
        var count = Value.Count;

        if (i < 0)
        {
            i += count;
        }

        return new(i >= 0 && i < count ? NodeValues.ToFluidValue(Value[i]) : NilValue.Instance);
    }

    public override async ValueTask<bool> ContainsAsync(FluidValue value, TemplateContext context)
    {
        await foreach (var item in EnumerateAsync(context))
        {
            if (item.Equals(value))
            {
                return true;
            }
        }

        return false;
    }

    public override async IAsyncEnumerable<FluidValue> EnumerateAsync(TemplateContext context)
    {
        foreach (var item in Value)
        {
            yield return NodeValues.ToFluidValue(item);
        }

        await Task.CompletedTask;
    }

    public override bool Equals(FluidValue other)
    {
        return other is NodeArrayValue nodeArray && (ReferenceEquals(nodeArray.Value, Value) || nodeArray.Value.Equals(Value));
    }

    [Obsolete("Use the TemplateContext overload.")]
    public override bool ToBooleanValue() => true;

    [Obsolete("Use the TemplateContext overload.")]
    public override decimal ToNumberValue() => 0;

    [Obsolete("Use the TemplateContext overload.")]
    public override string ToStringValue() => string.Concat(Value.Select(x => NodeValues.ToFluidValue(x).ToStringValue()));

    [Obsolete("Use the TemplateContext overload.")]
    public override object ToObjectValue() => Value;

    public override async ValueTask WriteToAsync(IFluidOutput output, TextEncoder encoder, CultureInfo cultureInfo)
    {
        foreach (var item in Value)
        {
            await NodeValues.ToFluidValue(item).WriteToAsync(output, encoder, cultureInfo);
        }
    }

    public override bool Equals(object? obj) => obj is FluidValue other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();
}
