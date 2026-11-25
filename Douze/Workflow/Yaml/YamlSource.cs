using System.Globalization;
using Douze.Nodes;

namespace Douze.Workflow.Yaml;

/// <summary>
/// A parsed YAML file that keeps the position of every node, so that errors point at the file, line and column of
/// the node they are about.
/// </summary>
internal sealed class YamlSource
{
    private readonly YamlStream _stream;

    private YamlSource(string fileName, YamlStream stream)
    {
        FileName = fileName;
        _stream = stream;
        Root = stream.Documents.IsEmpty ? null : stream.Documents[0];
    }

    /// <summary>
    /// The name used in locations, such as <c>douze.yml</c>.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// The content of the document, or <c>null</c> when the file is empty.
    /// </summary>
    public YamlSyntax? Root { get; }

    /// <exception cref="WorkflowValidationException">The text is not valid YAML or has more than one document.</exception>
    public static YamlSource Parse(string text, string fileName)
    {
        YamlStream stream;

        try
        {
            stream = YamlStream.Parse(text);
        }
        catch (YamlSyntaxException e)
        {
            throw new WorkflowValidationException(e.Reason, new SourceLocation(fileName, e.Line, e.Column));
        }

        var source = new YamlSource(fileName, stream);

        if (stream.Documents.Length > 1)
        {
            throw new WorkflowValidationException("a workflow file has a single YAML document", source.Location(stream.Documents[1]));
        }

        return source;
    }

    public SourceLocation Location(YamlSyntax node)
    {
        var (line, column) = _stream.Position(node);
        return new SourceLocation(FileName, line, column);
    }

    /// <summary>
    /// Whether <paramref name="node"/> is <c>null</c>, <c>~</c> or empty (an absent value).
    /// </summary>
    public static bool IsNull(YamlSyntax? node)
    {
        return node is null || node is YamlScalar { IsPlain: true } scalar && IsNullLiteral(scalar.Value);
    }

    /// <summary>
    /// The text of a scalar, or <c>null</c> when <paramref name="node"/> is not a scalar.
    /// </summary>
    public static string? Scalar(YamlSyntax node) => node is YamlScalar scalar ? scalar.Value : null;

    /// <summary>
    /// The entries of a mapping in document order. A key that is not a scalar or that is defined twice is an error.
    /// </summary>
    public IEnumerable<(string Key, YamlSyntax KeyNode, YamlSyntax Value)> Entries(YamlMapping mapping, ICollection<WorkflowValidationError> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (keyNode, value) in mapping)
        {
            if (keyNode is not YamlScalar { Value: var key })
            {
                errors.Add(new("keys must be strings", Location(keyNode)));
                continue;
            }

            if (!seen.Add(key))
            {
                errors.Add(new($"duplicate key '{key}'", Location(keyNode)));
                continue;
            }

            yield return (key, keyNode, value);
        }
    }

    /// <summary>
    /// Converts a node to a <see cref="Node"/>, typing plain scalars like data files do: integers, decimals, booleans
    /// and null.
    /// </summary>
    public Node? ToNode(YamlSyntax? node, ICollection<WorkflowValidationError> errors)
    {
        switch (node)
        {
            case null:
                return null;

            case YamlMapping mapping:
                var obj = new NodeObject();

                foreach (var (key, _, value) in Entries(mapping, errors))
                {
                    obj[key] = ToNode(value, errors);
                }

                return obj;

            case YamlSequence sequence:
                var array = new NodeArray();

                foreach (var item in sequence)
                {
                    array.Add(ToNode(item, errors));
                }

                return array;

            case YamlScalar { IsPlain: true } scalar:
                return TypedScalar(scalar.Value);

            case YamlScalar scalar:
                return scalar.Value;

            default:
                return null;
        }
    }

    /// <summary>
    /// Whether a plain scalar with this text would not be read as a string (a number, a boolean or null), and so must
    /// be quoted to stay a string.
    /// </summary>
    public static bool IsTyped(string text) => text.Length == 0 || TypedScalar(text) is not NodeValue<string>;

    private static Node? TypedScalar(string text)
    {
        if (IsNullLiteral(text))
        {
            return null;
        }

        if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i))
        {
            return i;
        }

        if (decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d))
        {
            return d;
        }

        if (bool.TryParse(text, out var b))
        {
            return b;
        }

        return text;
    }

    private static bool IsNullLiteral(string text) => text is "" or "~" or "null" or "Null" or "NULL";
}
