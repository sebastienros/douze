using Fluid;
using Fluid.Values;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Zio;

namespace Douze;

internal static class LayoutHelper
{
    public static async Task<string> ProcessLayoutAsync(Options options, string content, JsonObject data, Dictionary<string, FluidValue> captures)
    {
        if (!data.TryGetPropertyValue("layout", out var layoutValue) || layoutValue == null)
        {
            // No layout to apply
            return content;
        }

        var layout = layoutValue.ToString();

        if (string.IsNullOrWhiteSpace(layout))
        {
            return content;
        }

        var layoutFilePath = UPath.Combine(options.IncludesFolder.ToAbsolute(), layout);

        if (!options.PropertyBag.TryGetValue(layoutFilePath.ToString(), out var cache) || !(cache is CachedLayout cachedLayout))
        {
            using var stream = options.SourceFileSystem.OpenFile(layoutFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var streamReader = new StreamReader(stream);
            var result = await FrontMatterHelper.ParseFrontMatterAsync(streamReader, options);

            var template = options.FluidParser.Parse(result.Content);

            var frontMatter = !String.IsNullOrEmpty(result.FrontMatter) ? YamlHelper.Parse(result.FrontMatter) : new JsonObject();

            cachedLayout = new CachedLayout(frontMatter, template);

            options.PropertyBag[layoutFilePath.ToString()] = cachedLayout;
        }

        var localData = cachedLayout.Data;

        // Remove layout property from markdown data to prevent loss of base layout (layout chaining)
        data.Remove("layout");

        JsonHelper.Merge(localData, data);

        var context = new TemplateContext(localData, options.TemplateOptions);

        foreach (var capture in captures)
        {
            context.SetValue(capture.Key, capture.Value);
        }

        context.SetValue("content", new StringValue(content, false));

        var html = await cachedLayout.Template.RenderAsync(context, HtmlEncoder.Default);

        // Check if the evaluated layout has a base layout

        if (!localData.TryGetPropertyValue("layout", out layoutValue))
        {
            return html;
        }

        captures = new Dictionary<string, FluidValue>();

        foreach (var name in context.ValueNames)
        {
            captures.Add(name, context.GetValue(name));
        }

        // Apply layout chaining

        return await ProcessLayoutAsync(options, html, localData, captures);
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
