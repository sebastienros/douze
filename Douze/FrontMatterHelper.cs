using System.Text.Json.Nodes;
using Douze.FileSystems;

namespace Douze;

internal static class FrontMatterHelper
{
    public static async Task<(string FrontMatter, string Content)> ParseFrontMatterAsync(TextReader reader, Options options)
    {
        var content = await reader.ReadToEndAsync();

        var fmStart = content.IndexOf("---");

        if (content.Length < 6 || fmStart == -1)
        {
            return ("", content);
        }

        var fmEnd = content.IndexOf("---", fmStart + 4);

        if (fmEnd == -1)
        {
            return ("", content);
        }

        // Check --- appears at the start of a line

        if ((fmStart == 0 || content[fmStart - 1] == '\n') && (content[fmStart + 3] == '\n' || content[fmStart + 4] == '\n') && content[fmEnd - 1] == '\n' && (content[fmEnd + 3] == '\n' || content[fmEnd + 4] == '\n'))
        {
            var docStart = content.IndexOf('\n', fmEnd + 1) + 1;
            fmStart = content.IndexOf('\n', fmStart + 1) + 1;

            var fm = content.Substring(fmStart, fmEnd - fmStart).Trim();
            var doc = content.Substring(docStart);

            return (fm, doc);
        }

        return ("", content);
    }

    /// <summary>
    /// Reads the FrontMatter block of a file, and returns the rest of the content.
    /// </summary>
    /// <param name="options"></param>
    /// <param name="data"></param>
    /// <param name="filePath"></param>
    /// <returns></returns>
    public static async Task<string> MergeFrontMatterData(Options options, JsonObject data, Stream stream)
    {
        using var reader = new StreamReader(stream);
        var (frontMatter, content) = await ParseFrontMatterAsync(reader, options);

        if (!string.IsNullOrWhiteSpace(frontMatter))
        {
            YamlHelper.MergeData(data, new StringReader(frontMatter));
        }

        return content;
    }
}
