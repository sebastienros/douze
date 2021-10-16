using Fluid;
using Fluid.Values;
using System.Text;
using System.Text.Json.Nodes;

namespace Douze
{
    internal sealed class FrontMatterHelper
    {
        static readonly TemplateOptions _options = new();

        static FrontMatterHelper()
        {
            // When a property of a JObject value is accessed, try to look into its properties
            _options.MemberAccessStrategy.Register<JsonObject, object?>((source, name) => source[name]);

            // Convert JToken to FluidValue
            _options.ValueConverters.Add(x => x is JsonObject o ? new ObjectValue(o) : null);
            _options.ValueConverters.Add(x => x is JsonValue v ? v.GetValue<object>() : null);
        }

        public static async Task<(string FrontMatter, string Content)> ParseFrontMatterAsync(TextReader reader)
        {
            var contentBuilder = new StringBuilder();
            var frontMatterBuilder = new StringBuilder();

            var line = await reader.ReadLineAsync();

            contentBuilder.AppendLine(line);

            // Read empty lines

            while (line != null && String.IsNullOrWhiteSpace(line))
            {
                contentBuilder.AppendLine(line = await reader.ReadLineAsync());
            }

            // If it's not "---" then there is no FM block
            if (line != null && line.Trim() == "---")
            {
                contentBuilder.Clear();
                line = await reader.ReadLineAsync();

                while (line != null && line != "---")
                {
                    frontMatterBuilder.AppendLine(line);

                    line = await reader.ReadLineAsync();
                }

                if (line == null)
                {
                    contentBuilder.Append(frontMatterBuilder);
                    frontMatterBuilder.Clear();
                }
            }

            while (line != null)
            {
                contentBuilder.AppendLine(line = await reader.ReadLineAsync());
            }

            return (frontMatterBuilder.ToString(), contentBuilder.ToString());
        }

        /// <summary>
        /// Reads the FrontMatter block of a file, and returns the rest of the content.
        /// </summary>
        /// <param name="options"></param>
        /// <param name="data"></param>
        /// <param name="filePath"></param>
        /// <returns></returns>
        public static async Task<string> MergeFrontMatterData(Options options, JsonObject data, string filePath)
        {
            using (var reader = File.OpenText(filePath))
            {
                var yaml = await FrontMatterHelper.ParseFrontMatterAsync(reader);

                if (!String.IsNullOrWhiteSpace(yaml.FrontMatter))
                {
                    // Convert from Liquid
                    yaml.FrontMatter = await options.FluidParser.Parse(yaml.FrontMatter).RenderAsync(new TemplateContext(data, _options));

                    YamlHelper.MergeData(data, new StringReader(yaml.FrontMatter));
                }

                return yaml.Content;
            }
        }
    }
}
