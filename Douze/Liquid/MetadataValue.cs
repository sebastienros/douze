using System.Globalization;
using System.Text.Encodings.Web;
using Douze.Nodes;
using Douze.Workflow;
using Fluid;
using Fluid.Values;

namespace Douze.Liquid;

/// <summary>
/// The model of a render: resolves names through the <see cref="ScopeSet"/> and records dependencies in the
/// <see cref="DependencyTracker"/> found in the context's ambient values.
/// </summary>
internal sealed class MetadataValue : FluidValue
{
    private readonly NodeObject? _fallback;

    // fallback: Values with the lowest precedence, such as the front matter of a layout.
    public MetadataValue(Metadata metadata, NodeObject? fallback = null)
    {
        Metadata = metadata;
        _fallback = fallback;
    }

    public Metadata Metadata { get; }

    public override FluidValues Type => FluidValues.Dictionary;

    public override ValueTask<FluidValue> GetValueAsync(string name, TemplateContext context)
    {
        var scopes = context.AmbientValues.TryGetValue(AmbientKeys.Scopes, out var s) && s is ScopeSet scopeSet ? scopeSet : ScopeSet.Empty;
        var deps = context.AmbientValues.TryGetValue(AmbientKeys.Dependencies, out var d) ? d as DependencyTracker : null;

        // The unit depends on what the template reads of a value from the scopes, not on all of it.
        if (scopes.TryResolveTracked(Metadata, name, _fallback, deps, out var node, out var recorder))
        {
            return new(NodeValues.ToFluidValue(node, recorder));
        }

        return new(NilValue.Instance);
    }

    public override ValueTask<FluidValue> GetIndexAsync(FluidValue index, TemplateContext context)
    {
        return GetValueAsync(index.ToStringValue(context), context);
    }

    // Enumerating would mean reading every scope without tracking: not supported.
    public override async IAsyncEnumerable<FluidValue> EnumerateAsync(TemplateContext context)
    {
        await Task.CompletedTask;
        yield break;
    }

    public override bool Equals(FluidValue other) => other is MetadataValue metadata && ReferenceEquals(metadata.Metadata, Metadata);

    [Obsolete("Use the TemplateContext overload.")]
    public override bool ToBooleanValue() => true;

    [Obsolete("Use the TemplateContext overload.")]
    public override decimal ToNumberValue() => 0;

    [Obsolete("Use the TemplateContext overload.")]
    public override string ToStringValue() => "";

    [Obsolete("Use the TemplateContext overload.")]
    public override object ToObjectValue() => Metadata;

    public override ValueTask WriteToAsync(IFluidOutput output, TextEncoder encoder, CultureInfo cultureInfo) => default;

    public override bool Equals(object? obj) => obj is FluidValue other && Equals(other);

    public override int GetHashCode() => Metadata.GetHashCode();
}
