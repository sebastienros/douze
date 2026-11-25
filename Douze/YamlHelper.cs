using System.Text;
using Douze.Nodes;
using Douze.Workflow.Yaml;

namespace Douze;

/// <summary>
/// Reads the YAML of front matter and data files into nodes.
/// </summary>
internal static class YamlHelper
{
    /// <exception cref="YamlSyntaxException">The text is not valid YAML, or has a key that is not a scalar.</exception>
    public static Node? Parse(string content) => Parse(Encoding.UTF8.GetBytes(content));

    /// <param name="utf8">The UTF-8 text, with or without a byte order mark.</param>
    /// <exception cref="YamlSyntaxException">The text is not valid YAML, or has a key that is not a scalar.</exception>
    public static Node? Parse(ReadOnlyMemory<byte> utf8)
    {
        var stream = YamlStream.Parse(utf8);

        // An empty document has no contents; the documents after the first one are ignored.
        return stream.Documents.IsEmpty ? null : Convert(stream, stream.Documents[0]);
    }

    /// <summary>
    /// Transforms a YAML node into a node: plain scalars are integers, decimals or booleans when they parse as one.
    /// </summary>
    private static Node? Convert(YamlStream stream, YamlSyntax node)
    {
        switch (node)
        {
            case YamlMapping mapping:
                var dictionary = new NodeObject();

                foreach (var (key, value) in mapping)
                {
                    if (key is not YamlScalar { Value: var name })
                    {
                        var (line, column) = stream.Position(key);
                        throw new YamlSyntaxException(line, column, "keys must be strings");
                    }

                    dictionary[name] = Convert(stream, value);
                }

                return dictionary;

            case YamlSequence sequence:
                var array = new NodeArray();

                foreach (var item in sequence)
                {
                    array.Add(Convert(stream, item));
                }

                return array;

            case YamlScalar { IsPlain: true, Value: var text }:
                if (int.TryParse(text, out var intValue))
                {
                    return intValue;
                }
                else if (decimal.TryParse(text, out var decimalValue))
                {
                    return decimalValue;
                }
                else if (bool.TryParse(text, out var boolValue))
                {
                    return boolValue;
                }

                return text;

            case YamlScalar scalar:
                return scalar.Value;

            default:
                return null;
        }
    }
}
