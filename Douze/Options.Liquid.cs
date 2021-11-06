using Douze.Liquid;
using Douze.Nodes;
using Fluid;
using Fluid.Values;

namespace Douze;

internal partial class Options
{
    public TemplateOptions TemplateOptions { get; } = new();

    private Options()
    {
        // When a property of a JObject value is accessed, try to look into its properties
        TemplateOptions.MemberAccessStrategy.Register<NodeObject, object?>((source, name) => source[name]);

        // Convert JToken to FluidValue
        TemplateOptions.ValueConverters.Add(x => x is NodeObject o ? new ObjectValue(o) : null);
        TemplateOptions.ValueConverters.Add(x => x is NodeValue v ? v.GetValue<object>() : null);

        TemplateOptions.Filters.AddFilter("slug", SlugFilter.Slugify);
    }
}
