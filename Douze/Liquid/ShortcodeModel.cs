using System.Globalization;
using System.Text.Encodings.Web;
using Fluid;
using Fluid.Values;

namespace Douze.Liquid;

/// <summary>
/// The model a shortcode template renders with: its own names first (<c>body</c>, <c>source</c>, <c>args</c>, the
/// named arguments, the positional ones named by its front matter), then the page's model, so that a shortcode reads
/// <c>page.url</c>, data and collections like an include does, with the same dependency recording.
/// </summary>
internal sealed class ShortcodeModel : FluidValue
{
    private readonly Dictionary<string, FluidValue> _values;
    private readonly FluidValue? _page;

    public ShortcodeModel(Dictionary<string, FluidValue> values, FluidValue? page)
    {
        _values = values;
        _page = page;
    }

    public override FluidValues Type => FluidValues.Dictionary;

    public override ValueTask<FluidValue> GetValueAsync(string name, TemplateContext context)
    {
        if (_values.TryGetValue(name, out var value))
        {
            return new(value);
        }

        return _page == null ? new(NilValue.Instance) : _page.GetValueAsync(name, context);
    }

    public override ValueTask<FluidValue> GetIndexAsync(FluidValue index, TemplateContext context)
    {
        return GetValueAsync(index.ToStringValue(context), context);
    }

    // The page's names cannot be enumerated (see MetadataValue).
    public override async IAsyncEnumerable<FluidValue> EnumerateAsync(TemplateContext context)
    {
        await Task.CompletedTask;
        yield break;
    }

    public override bool Equals(FluidValue other) => ReferenceEquals(this, other);

    [Obsolete("Use the TemplateContext overload.")]
    public override bool ToBooleanValue() => true;

    [Obsolete("Use the TemplateContext overload.")]
    public override decimal ToNumberValue() => 0;

    [Obsolete("Use the TemplateContext overload.")]
    public override string ToStringValue() => "";

    [Obsolete("Use the TemplateContext overload.")]
    public override object ToObjectValue() => _values;

    public override ValueTask WriteToAsync(IFluidOutput output, TextEncoder encoder, CultureInfo cultureInfo) => default;

    public override bool Equals(object? obj) => obj is FluidValue other && Equals(other);

    public override int GetHashCode() => _values.GetHashCode();
}
