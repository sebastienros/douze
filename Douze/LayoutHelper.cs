using System.Text.Encodings.Web;
using Douze.FileSystems;
using Douze.Nodes;
using Fluid;
using Fluid.Values;

namespace Douze;

internal static class LayoutHelper
{
    public static readonly string LayoutCacheKey = "$layout";
    private static readonly SemaphoreSlim _lock = new(1);

    public static async Task<string> ProcessLayoutAsync(Options options, string content, NodeObject data)
    {
        if (!data.TryGetItem("layout", out var layoutProperty) || layoutProperty == null)
        {
            // No layout to apply
            return content;
        }

        var layout = layoutProperty.GetValue<string>();

        if (string.IsNullOrWhiteSpace(layout))
        {
            return content;
        }

        var normalizedLayout = new FPath(layout, true).ToRelative().ToString();
        var cacheKey = new Options.PropertyBagKey(LayoutCacheKey, normalizedLayout);

        CachedLayout? cachedLayout = null;

        if (!options.PropertyBag.TryGetValue(cacheKey, out var cache))
        {
            // Prevent multiple threads from parsing the same layout

            await _lock.WaitAsync();

            try
            {
                foreach (var includeFolder in options.IncludesFolders)
                {
                    var layoutFilePath = FPath.Combine(includeFolder, new FPath(layout, true).ToRelative());

                    if (!(await options.SourceFileSystem.GetFileEntryAsync(layoutFilePath)).Exists)
                    {
                        continue;
                    }

                    using var stream = await options.SourceFileSystem.OpenFileAsync(layoutFilePath);
                    using var streamReader = new StreamReader(stream);
                    var result = await FrontMatterHelper.ParseFrontMatterAsync(streamReader, options);

                    var template = options.FluidParser.Parse(result.Content);

                    var frontMatter = !string.IsNullOrEmpty(result.FrontMatter) ? YamlHelper.Parse(result.FrontMatter)?.AsObject() : new NodeObject();

                    options.PropertyBag[cacheKey] = cachedLayout = new CachedLayout(frontMatter ?? new NodeObject(), template);
                }
            }
            finally
            {
                _lock.Release();
            }
        }
        else
        {
            cachedLayout = cache as CachedLayout;
        }

        if (cachedLayout == null)
        {
            throw new DouzeException($"Layout not found: '{layout}' in file ''");
        }


        // Clone the cache data since it will be altered to remove the layout
        var localData = cachedLayout.Data.Clone() as NodeObject ?? new NodeObject();

        // Remove layout property from markdown data to prevent loss of base layout (layout chaining)
        data.Remove("layout");

        localData.Merge(data);

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

    private record class CachedLayout(NodeObject Data, IFluidTemplate Template);

}
