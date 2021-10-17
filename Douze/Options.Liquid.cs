using Douze.Liquid;
using Fluid;
using Fluid.Values;
using System.Text.Json.Nodes;

namespace Douze;

internal partial class Options
{
    public TemplateOptions TemplateOptions { get; } = new();

    public Options()
    {
        // When a property of a JObject value is accessed, try to look into its properties
        TemplateOptions.MemberAccessStrategy.Register<JsonObject, object?>((source, name) => source[name]);

        // Convert JToken to FluidValue
        TemplateOptions.ValueConverters.Add(x => x is JsonObject o ? new ObjectValue(o) : null);
        TemplateOptions.ValueConverters.Add(x => x is JsonValue v ? v.GetValue<object>() : null);

        TemplateOptions.Filters.AddFilter("slug", SlugFilter.Slugify);
    }
}
