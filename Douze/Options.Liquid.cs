using Douze.Liquid;
using Fluid;

namespace Douze;

internal partial class Options
{
    public TemplateOptions TemplateOptions { get; } = new();

    private Options()
    {
        TemplateOptions.Filters.AddFilter("slug", SlugFilter.Slugify);
    }
}
