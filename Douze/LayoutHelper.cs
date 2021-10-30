using Douze.FileSystems;
using Fluid;
using Fluid.Values;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;

namespace Douze;

internal static class LayoutHelper
{
    public static async Task<string> ProcessLayoutAsync(Options options, string content, JsonObject data)
    {
        if (!data.TryGetPropertyValue("layout", out var layoutProperty) || layoutProperty == null)
        {
            // No layout to apply
            return content;
        }

        var layout = layoutProperty.GetValue<string>();

        if (string.IsNullOrWhiteSpace(layout))
        {
            return content;
        }

        var layoutFilePath = FPath.Combine(options.IncludesFolder, new FPath(layout, true).ToRelative());

        if (!options.PropertyBag.TryGetValue(layoutFilePath.ToString(), out var cache) || !(cache is CachedLayout cachedLayout))
        {
            using var stream = await options.SourceFileSystem.OpenFileAsync(layoutFilePath);
            using var streamReader = new StreamReader(stream);
            var result = await FrontMatterHelper.ParseFrontMatterAsync(streamReader, options);

            var template = options.FluidParser.Parse(result.Content);

            var frontMatter = !String.IsNullOrEmpty(result.FrontMatter) ? YamlHelper.Parse(result.FrontMatter) : new JsonObject();

            cachedLayout = new CachedLayout(frontMatter, template);

            options.PropertyBag[layoutFilePath.ToString()] = cachedLayout;
        }

        // Clone the cache data since it will be altered to remove the layout
        var localData = JsonHelper.Clone(cachedLayout.Data) as JsonObject ?? new JsonObject();

        // Remove layout property from markdown data to prevent loss of base layout (layout chaining)
        data.Remove("layout");

        JsonHelper.Merge(localData, data);

        var context = new TemplateContext(localData, options.TemplateOptions);

        context.SetValue("content", new StringValue(content, false));

        var html = await cachedLayout.Template.RenderAsync(context, HtmlEncoder.Default);

        // Check if the evaluated layout has a base layout

        if (!localData.ContainsKey("layout"))
        {
            return html;
        }

        // Apply layout chaining

        return await ProcessLayoutAsync(options, html, localData);
    }

    private class CachedLayout
    {
        public CachedLayout(JsonObject data, IFluidTemplate template)
        {
            Data = data;
            Template = template;
        }

        public JsonObject Data { get; set; }
        public IFluidTemplate Template { get; set; }
    }

}
