using Fluid;
using Fluid.Values;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;

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

        var layoutFilePath = Path.GetFullPath(Path.Combine(options.SourcePath, options.IncludesFolder, layout));

        using var reader = File.OpenText(layoutFilePath);
        var result = await FrontMatterHelper.ParseFrontMatterAsync(reader, options);

        var template = options.FluidParser.Parse(result.Content);

        var localData = result.FrontMatter != null ? YamlHelper.Parse(result.FrontMatter) : new JsonObject();

        // Remove layout property from markdown data to prevent loss of base layout (layout chaining)
        data.Remove("layout");

        JsonHelper.Merge(localData, data);

        var context = new TemplateContext(localData, options.TemplateOptions);

        context.SetValue("content", new StringValue(content, false));

        foreach (var capture in captures)
        {
            context.SetValue(capture.Key, capture.Value);
        }

        var html = await template.RenderAsync(context, HtmlEncoder.Default);

        captures = new Dictionary<string, FluidValue>();

        foreach (var name in context.ValueNames)
        {
            captures.Add(name, context.GetValue(name));
        }

        // Apply layout chaining
        return await ProcessLayoutAsync(options, html, localData, captures);
    }
}