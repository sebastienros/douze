using System.Buffers;
using Fluid;

namespace Douze.Liquid;

/// <summary>
/// An <see cref="IFluidOutput"/> that collects what is written, for tags that render their body to a string.
/// </summary>
internal sealed class StringFluidOutput : IFluidOutput
{
    private readonly ArrayBufferWriter<char> _buffer = new(256);

    public void Advance(int count) => _buffer.Advance(count);

    public Memory<char> GetMemory(int sizeHint = 0) => _buffer.GetMemory(sizeHint);

    public Span<char> GetSpan(int sizeHint = 0) => _buffer.GetSpan(sizeHint);

    public void Write(string value) => _buffer.Write(value.AsSpan());

    public void Write(char[] buffer, int index, int count) => _buffer.Write(buffer.AsSpan(index, count));

    public ValueTask FlushAsync() => default;

    public override string ToString() => new(_buffer.WrittenSpan);
}
