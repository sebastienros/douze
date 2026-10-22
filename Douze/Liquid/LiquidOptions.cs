using Douze.Liquid.Filters;
using Douze.Liquid.Tags;
using Fluid;

namespace Douze.Liquid;

/// <summary>
/// The Liquid configuration of the workflow engine.
/// </summary>
internal static class LiquidOptions
{
    public static TemplateOptions Create(TemplateLoader loader)
    {
        var builder = new TemplateOptionsBuilder()
            .AddFilter("slug", SlugFilter.Slugify)
            .WithJsonSerializerOptions(DouzeJsonContext.Default.Options)
            .WithFileProvider(loader)                 // {% include %} / {% render %} from the includes folders
            .WithTemplateCache(loader)                // includes are parsed once per content hash
            .AddValueConverter(NodeValues.Convert)    // Node -> FluidValue, Metadata -> MetadataValue
            .WithDefaultFileExtension(".liquid");

        HtmlFilters.Register(builder);
        ImageFilters.Register(builder);

        return builder.Build();
    }

    /// <summary>
    /// A parser whose errors carry line and column, with the tags of Douze.
    /// </summary>
    public static FluidParser CreateParser()
    {
#pragma warning disable FLUID001 // statement locations are what gives errors a line and a column
        var parser = new FluidParser(new FluidParserOptions { TrackStatementLocations = true });
#pragma warning restore FLUID001

        ImageTag.Register(parser);

        return parser;
    }
}
