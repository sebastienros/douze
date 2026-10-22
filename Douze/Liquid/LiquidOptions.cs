using Douze.Liquid.Filters;
using Fluid;

namespace Douze.Liquid;

/// <summary>
/// The Liquid configuration of the workflow engine.
/// </summary>
internal static class LiquidOptions
{
    public static TemplateOptions Create(TemplateLoader loader)
    {
        var builder = new TemplateOptionsBuilder().AddFilter("slug", SlugFilter.Slugify);

        DataFilters.Register(builder);
        OutputFilters.Register(builder);
        LocaleFilters.Register(builder);
        NavigationFilters.Register(builder);

        return builder
            .WithJsonSerializerOptions(DouzeJsonContext.Default.Options)
            .WithFileProvider(loader)                 // {% include %} / {% render %} from the includes folders
            .WithTemplateCache(loader)                // includes are parsed once per content hash
            .AddValueConverter(NodeValues.Convert)    // Node -> FluidValue, Metadata -> MetadataValue
            .WithDefaultFileExtension(".liquid")
            .Build();
    }

    /// <summary>
    /// A parser whose errors carry line and column.
    /// </summary>
#pragma warning disable FLUID001 // statement locations are what gives errors a line and a column
    public static FluidParser CreateParser() => new(new FluidParserOptions { TrackStatementLocations = true });
#pragma warning restore FLUID001
}
