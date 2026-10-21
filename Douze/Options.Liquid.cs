using Douze.Liquid;
using Fluid;

namespace Douze;

internal partial class Options
{
    public TemplateOptions TemplateOptions { get; } = new TemplateOptionsBuilder()
        .AddFilter("slug", SlugFilter.Slugify)
        .WithJsonSerializerOptions(DouzeJsonContext.Default.Options)
        .Build();
}
