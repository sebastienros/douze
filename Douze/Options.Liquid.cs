using Douze.Liquid;
using Douze.Nodes;
using Fluid;

namespace Douze;

internal partial class Options
{
    public TemplateOptions TemplateOptions { get; } = new TemplateOptionsBuilder()
        .AddFilter("slug", SlugFilter.Slugify)
        // Exposes the raw values (dates, numbers, ...) such that filters like 'sort' compare them by value
        .AddValueConverter(static x => x is NodeValue value ? value.GetValue<object>() : null)
        .WithJsonSerializerOptions(DouzeJsonContext.Default.Options)
        .Build();
}
