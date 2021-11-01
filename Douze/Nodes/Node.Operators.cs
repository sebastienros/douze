namespace Douze.Nodes;

internal abstract partial class Node
{
    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="bool"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="bool"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>
    public static implicit operator Node(bool value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="bool"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="bool"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>
    public static implicit operator Node?(bool? value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="byte"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="byte"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>
    public static implicit operator Node(byte value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="byte"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="byte"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>
    public static implicit operator Node?(byte? value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="char"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="char"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>
    public static implicit operator Node(char value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="char"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="char"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>
    public static implicit operator Node?(char? value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="DateTime"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="DateTime"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>
    public static implicit operator Node(DateTime value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="DateTime"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="DateTime"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>
    public static implicit operator Node?(DateTime? value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="DateTimeOffset"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="DateTimeOffset"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>
    public static implicit operator Node(DateTimeOffset value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="DateTimeOffset"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="DateTimeOffset"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>
    public static implicit operator Node?(DateTimeOffset? value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="decimal"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="decimal"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>
    public static implicit operator Node(decimal value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="decimal"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="decimal"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>
    public static implicit operator Node?(decimal? value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="double"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="double"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>
    public static implicit operator Node(double value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="double"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="double"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>
    public static implicit operator Node?(double? value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="Guid"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="Guid"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>
    public static implicit operator Node(Guid value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="Guid"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="Guid"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>
    public static implicit operator Node?(Guid? value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="short"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="short"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>
    public static implicit operator Node(short value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="short"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="short"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>
    public static implicit operator Node?(short? value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="int"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="int"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>
    public static implicit operator Node(int value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="int"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="int"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>
    public static implicit operator Node?(int? value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="long"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="long"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>
    public static implicit operator Node(long value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="long"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="long"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>
    public static implicit operator Node?(long? value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="sbyte"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="sbyte"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>

    public static implicit operator Node(sbyte value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="sbyte"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="sbyte"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>

    public static implicit operator Node?(sbyte? value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="float"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="float"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>
    public static implicit operator Node(float value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="float"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="float"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>
    public static implicit operator Node?(float? value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="string"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="string"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>
    public static implicit operator Node?(string? value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="ushort"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="ushort"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>

    public static implicit operator Node(ushort value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="ushort"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="ushort"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>

    public static implicit operator Node?(ushort? value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="uint"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="uint"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>

    public static implicit operator Node(uint value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="uint"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="uint"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>

    public static implicit operator Node?(uint? value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="ulong"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="ulong"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>

    public static implicit operator Node(ulong value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an implicit conversion of a given <see cref="ulong"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="ulong"/> to implicitly convert.</param>
    /// <returns>A <see cref="Node"/> instance converted from the <paramref name="value"/> parameter.</returns>

    public static implicit operator Node?(ulong? value) => NodeValue.Create(value);

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="bool"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="bool"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator bool(Node value) => value.GetValue<bool>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="bool"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="bool"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator bool?(Node? value) => value?.GetValue<bool>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="byte"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="byte"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator byte(Node value) => value.GetValue<byte>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="byte"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="byte"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator byte?(Node? value) => value?.GetValue<byte>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="char"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="char"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator char(Node value) => value.GetValue<char>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="char"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="char"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator char?(Node? value) => value?.GetValue<char>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="DateTime"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="DateTime"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator DateTime(Node value) => value.GetValue<DateTime>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="DateTime"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="DateTime"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator DateTime?(Node? value) => value?.GetValue<DateTime>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="DateTimeOffset"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="DateTimeOffset"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator DateTimeOffset(Node value) => value.GetValue<DateTimeOffset>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="DateTimeOffset"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="DateTimeOffset"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator DateTimeOffset?(Node? value) => value?.GetValue<DateTimeOffset>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="decimal"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="decimal"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator decimal(Node value) => value.GetValue<decimal>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="decimal"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="decimal"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator decimal?(Node? value) => value?.GetValue<decimal>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="double"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="double"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator double(Node value) => value.GetValue<double>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="double"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="double"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator double?(Node? value) => value?.GetValue<double>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="Guid"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="Guid"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator Guid(Node value) => value.GetValue<Guid>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="Guid"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="Guid"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator Guid?(Node? value) => value?.GetValue<Guid>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="short"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="short"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator short(Node value) => value.GetValue<short>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="short"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="short"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator short?(Node? value) => value?.GetValue<short>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="int"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="int"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator int(Node value) => value.GetValue<int>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="int"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="int"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator int?(Node? value) => value?.GetValue<int>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="long"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="long"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator long(Node value) => value.GetValue<long>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="long"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="long"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator long?(Node? value) => value?.GetValue<long>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="sbyte"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="sbyte"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator sbyte(Node value) => value.GetValue<sbyte>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="sbyte"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="sbyte"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator sbyte?(Node? value) => value?.GetValue<sbyte>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="float"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="float"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator float(Node value) => value.GetValue<float>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="float"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="float"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator float?(Node? value) => value?.GetValue<float>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="string"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="string"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator string?(Node? value) => value?.GetValue<string>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="ushort"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="ushort"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator ushort(Node value) => value.GetValue<ushort>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="ushort"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="ushort"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator ushort?(Node? value) => value?.GetValue<ushort>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="uint"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="uint"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator uint(Node value) => value.GetValue<uint>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="uint"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="uint"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator uint?(Node? value) => value?.GetValue<uint>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="ulong"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="ulong"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator ulong(Node value) => value.GetValue<ulong>();

    /// <summary>
    ///   Defines an explicit conversion of a given <see cref="ulong"/> to a <see cref="Node"/>.
    /// </summary>
    /// <param name="value">A <see cref="ulong"/> to explicitly convert.</param>
    /// <returns>A value converted from the <see cref="Node"/> instance.</returns>
    public static explicit operator ulong?(Node? value) => value?.GetValue<ulong>();
}
