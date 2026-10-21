using Douze.Nodes;

namespace Douze;

/// <summary>
/// Splits the YAML front matter block (between two <c>---</c> lines) from the rest of a file.
/// </summary>
internal static class FrontMatter
{
    /// <summary>
    /// Returns the front matter values (empty when there is no front matter) and the content without the block.
    /// </summary>
    public static (NodeObject Values, string Content) Parse(string text)
    {
        var (frontMatter, content) = Split(text);

        if (string.IsNullOrWhiteSpace(frontMatter))
        {
            return (new NodeObject(), content);
        }

        return YamlHelper.Parse(frontMatter) switch
        {
            NodeObject values => (values, content),
            null => (new NodeObject(), content),
            _ => throw new DouzeException("the front matter must be a mapping"),
        };
    }

    /// <summary>
    /// Returns the raw front matter (trimmed, empty when there is none) and the content without the block.
    /// </summary>
    public static (string FrontMatter, string Content) Split(string content)
    {
        var fmStart = content.IndexOf("---", StringComparison.Ordinal);

        if (content.Length < 6 || fmStart == -1)
        {
            return ("", content);
        }

        var fmEnd = content.IndexOf("---", fmStart + 4, StringComparison.Ordinal);

        if (fmEnd == -1)
        {
            return ("", content);
        }

        // Check --- appears at the start of a line. The end of the content counts as the end of a line.

        if ((fmStart == 0 || content[fmStart - 1] == '\n') && (At(fmStart + 3) == '\n' || At(fmStart + 4) == '\n') && content[fmEnd - 1] == '\n' && (At(fmEnd + 3) == '\n' || At(fmEnd + 4) == '\n'))
        {
            var lineEnd = content.IndexOf('\n', fmEnd + 1);
            var docStart = lineEnd == -1 ? content.Length : lineEnd + 1;
            fmStart = content.IndexOf('\n', fmStart + 1) + 1;

            var fm = content[fmStart..fmEnd].Trim();
            var doc = content[docStart..];

            return (fm, doc);
        }

        return ("", content);

        char At(int index) => index < content.Length ? content[index] : '\n';
    }
}
