using System.Globalization;
using System.Text.RegularExpressions;
using Douze.Nodes;
using SharpYaml;
using SharpYaml.Model;

namespace Douze.Workflow.Yaml;

/// <summary>
/// A parsed YAML file that keeps the position of every node, so that errors point at the file, line and column of
/// the node they are about.
/// </summary>
internal sealed partial class YamlSource
{
    private YamlSource(string fileName, YamlElement? root)
    {
        FileName = fileName;
        Root = root;
    }

    /// <summary>
    /// The name used in locations, such as <c>douze.yml</c>.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// The content of the document, or <c>null</c> when the file is empty.
    /// </summary>
    public YamlElement? Root { get; }

    /// <exception cref="WorkflowValidationException">The text is not valid YAML or has more than one document.</exception>
    public static YamlSource Parse(string text, string fileName)
    {
        YamlStream stream;

        try
        {
            using var reader = new StringReader(text);
            stream = YamlStream.Load(reader);
        }
        catch (YamlException e)
        {
            throw new WorkflowValidationException(ErrorMessage(e.Message), Location(fileName, e.Start));
        }

        if (stream.Count > 1)
        {
            var second = stream[1].Contents;
            throw new WorkflowValidationException("a workflow file has a single YAML document", second is null ? null : Location(fileName, Start(second)));
        }

        return new YamlSource(fileName, stream.Count == 0 ? null : stream[0].Contents);
    }

    public SourceLocation Location(YamlElement node) => Location(FileName, Start(node));

    /// <summary>
    /// Whether <paramref name="node"/> is <c>null</c>, <c>~</c> or empty (an absent value).
    /// </summary>
    public static bool IsNull(YamlElement? node)
    {
        return node is null || node is YamlValue { Style: ScalarStyle.Plain or ScalarStyle.Any } value && IsNullLiteral(value.Value);
    }

    /// <summary>
    /// The text of a scalar, or <c>null</c> when <paramref name="node"/> is not a scalar.
    /// </summary>
    public static string? Scalar(YamlElement node) => node is YamlValue value ? value.Value : null;

    /// <summary>
    /// The entries of a mapping in document order. A key that is not a scalar or that is defined twice is an error.
    /// </summary>
    public IEnumerable<(string Key, YamlElement KeyNode, YamlElement? Value)> Entries(YamlMapping mapping, ICollection<WorkflowValidationError> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (keyNode, value) in mapping)
        {
            if (keyNode is not YamlValue { Value: var key })
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
    public Node? ToNode(YamlElement? node, ICollection<WorkflowValidationError> errors)
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

            case YamlValue { Style: ScalarStyle.Plain or ScalarStyle.Any } value:
                return TypedScalar(value.Value);

            case YamlValue value:
                return value.Value;

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

    internal static SharpYaml.Mark Start(YamlElement node)
    {
        return node switch
        {
            YamlValue value => value.Scalar.Start,
            YamlMapping mapping => mapping.MappingStart.Start,
            YamlSequence sequence => sequence.SequenceStart.Start,
            _ => SharpYaml.Mark.Empty,
        };
    }

    // SharpYaml marks are zero based.
    private static SourceLocation Location(string fileName, SharpYaml.Mark mark) => new(fileName, mark.Line + 1, mark.Column + 1);

    /// <summary>
    /// SharpYaml messages start with the positions, which the location already gives:
    /// <c>(Lin: 1, Col: 0, Chr: 7) - (Lin: 1, Col: 0, Chr: 7): While parsing a node, did not find expected node content.</c>
    /// </summary>
    internal static string ErrorMessage(string message)
    {
        message = MarksPrefix().Replace(message, "").Trim().TrimEnd('.');
        return message.Length == 0 ? "invalid YAML" : char.ToLowerInvariant(message[0]) + message[1..];
    }

    [GeneratedRegex(@"^\(Lin: \d+, Col: \d+, Chr: \d+\) - \(Lin: \d+, Col: \d+, Chr: \d+\): ")]
    private static partial Regex MarksPrefix();
}
