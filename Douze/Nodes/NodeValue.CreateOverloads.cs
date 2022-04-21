namespace Douze.Nodes;

internal partial class NodeValue
{
    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue Create(bool value) => new NodeValue<bool>(value);

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue? Create(bool? value) => value.HasValue ? new NodeValue<bool>(value.Value) : null;

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue Create(byte value) => new NodeValue<byte>(value);

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue? Create(byte? value) => value.HasValue ? new NodeValue<byte>(value.Value) : null;

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue Create(char value) => new NodeValue<char>(value);

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue? Create(char? value) => value.HasValue ? new NodeValue<char>(value.Value) : null;

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue Create(DateTime value) => new NodeValue<DateTime>(value);

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue? Create(DateTime? value) => value.HasValue ? new NodeValue<DateTime>(value.Value) : null;

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue Create(DateTimeOffset value) => new NodeValue<DateTimeOffset>(value);

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue? Create(DateTimeOffset? value) => value.HasValue ? new NodeValue<DateTimeOffset>(value.Value) : null;

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue Create(decimal value) => new NodeValue<decimal>(value);

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue? Create(decimal? value) => value.HasValue ? new NodeValue<decimal>(value.Value) : null;

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue Create(double value) => new NodeValue<double>(value);

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue? Create(double? value) => value.HasValue ? new NodeValue<double>(value.Value) : null;

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue Create(Guid value) => new NodeValue<Guid>(value);

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue? Create(Guid? value) => value.HasValue ? new NodeValue<Guid>(value.Value) : null;

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue Create(short value) => new NodeValue<short>(value);

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue? Create(short? value) => value.HasValue ? new NodeValue<short>(value.Value) : null;

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue Create(int value) => new NodeValue<int>(value);

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue? Create(int? value) => value.HasValue ? new NodeValue<int>(value.Value) : null;

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue Create(long value) => new NodeValue<long>(value);

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue? Create(long? value) => value.HasValue ? new NodeValue<long>(value.Value) : null;

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>

    public static NodeValue Create(sbyte value) => new NodeValue<sbyte>(value);

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>

    public static NodeValue? Create(sbyte? value) => value.HasValue ? new NodeValue<sbyte>(value.Value) : null;

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue Create(float value) => new NodeValue<float>(value);

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue? Create(float? value) => value.HasValue ? new NodeValue<float>(value.Value) : null;

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue? Create(string? value) => value != null ? new NodeValue<string>(value) : null;

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue Create(ushort value) => new NodeValue<ushort>(value);

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue? Create(ushort? value) => value.HasValue ? new NodeValue<ushort>(value.Value) : null;

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue Create(uint value) => new NodeValue<uint>(value);

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue? Create(uint? value) => value.HasValue ? new NodeValue<uint>(value.Value) : null;

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue Create(ulong value) => new NodeValue<ulong>(value);

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue? Create(ulong? value) => value.HasValue ? new NodeValue<ulong>(value.Value) : null;

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeValue"/> class that contains the specified value.
    /// </summary>
    /// <param name="value">The underlying value of the new <see cref="NodeValue"/> instance.</param>
    /// <returns>The new instance of the <see cref="NodeValue"/> class that contains the specified value.</returns>
    public static NodeValue? Create(object? value)
    {
        if (value == null)
        {
            return null;
        }

        var typeCode = Type.GetTypeCode(value.GetType());

        return typeCode switch
        {
            TypeCode.Boolean => Create((bool)value),
            TypeCode.Char => Create((char)value),
            TypeCode.SByte => Create((sbyte)value),
            TypeCode.Byte => Create((byte)value),
            TypeCode.Int16 => Create((short)value),
            TypeCode.UInt16 => Create((ushort)value),
            TypeCode.Int32 => Create((int)value),
            TypeCode.UInt32 => Create((uint)value),
            TypeCode.Int64 => Create((long)value),
            TypeCode.UInt64 => Create((ulong)value),
            TypeCode.Single => Create((float)value),
            TypeCode.Double => Create((double)value),
            TypeCode.Decimal => Create((decimal)value),
            TypeCode.DateTime => Create((DateTime)value),
            TypeCode.String => Create((string)value),
            _ => value switch
            {
                Guid => Create((Guid)value),
                DateTimeOffset => Create((DateTimeOffset)value),
                _ => throw new InvalidOperationException($"Can't convert a {value.GetType()} to a NodeValue"),
            },
        };
    }
}
