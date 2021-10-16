using Fluid;
using System.Text;
using System.Text.Json.Nodes;

namespace Douze
{
    internal sealed class FrontMatterHelper
    {
        public static async Task<(string FrontMatter, string Content)> ParseFrontMatterAsync(TextReader reader, Options options)
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
                var yaml = await FrontMatterHelper.ParseFrontMatterAsync(reader, options);

                if (!String.IsNullOrWhiteSpace(yaml.FrontMatter))
                {
                    YamlHelper.MergeData(data, new StringReader(yaml.FrontMatter));
                }

                return yaml.Content;
            }
        }
    }
}
