using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Douze.Nodes;

[DebuggerDisplay("{_value.ToString(),nq}")]
internal partial class NodeValue<TValue> : NodeValue, IFormattable
{
    private readonly TValue _value; // keep as a field for direct access to avoid copies

    public NodeValue(TValue value)
    {
        Debug.Assert(value != null);

        _value = value;
    }

    public TValue Value
    {
        get
        {
            return _value;
        }
    }

    public override T GetValue<T>()
    {
        // If no conversion is needed, just return the raw value.
        if (_value is T returnValue)
        {
            return returnValue;
        }

        // Currently we do not support other conversions.
        // Generics (and also boxing) do not support standard cast operators say from 'long' to 'int',
        //  so attempting to cast here would throw InvalidCastException.
        throw new InvalidOperationException($"Invalid conversion from {_value?.GetType()} to {typeof(T).GetType()}");
    }

    public override bool TryGetValue<T>([NotNullWhen(true)] out T value)
    {
        // If no conversion is needed, just return the raw value.
        if (_value is T returnValue)
        {
            value = returnValue;
            return true;
        }

        // Currently we do not support other conversions.
        // Generics (and also boxing) do not support standard cast operators say from 'long' to 'int',
        //  so attempting to cast here would throw InvalidCastException.
        value = default!;
        return false;
    }

    public override Node Clone()
    {
        var value = new NodeValue<TValue>(_value);

        return value;
    }

    public override bool Equals(Node? other)
    {
        if (other is not NodeValue<TValue> nodeValue || nodeValue == null)
        {
            return false;
        }

        if (nodeValue._value == null)
        {
            return _value == null;
        }

        return nodeValue._value.Equals(this._value);
    }

    public override int GetHashCode()
    {
        if (_value == null)
        {
            return 0;
        }

        return _value.GetHashCode();
    }

    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        return _value?.ToString() ?? "";
    }
}
