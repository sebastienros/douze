using System.Collections;
using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Yaml;

namespace Douze.Workflow.Yaml;

/// <summary>
/// The documents of a YAML text, read with Styml's <see cref="Utf8YamlReader"/> into nodes that keep their position,
/// so that errors point at the line and column of the node they are about. Every scalar is text: what it stands for
/// (a number, a boolean, null) is for the reader of the nodes to decide. Aliases are resolved to the node of their
/// anchor; <c>&lt;&lt;</c> is an ordinary key.
/// </summary>
internal sealed partial class YamlStream
{
    // The positions of the nodes are byte offsets in the text, turned into lines and columns when an error needs one.
    private readonly ReadOnlyMemory<byte> _utf8;
    private int[]? _lineStarts;

    private YamlStream(ReadOnlyMemory<byte> utf8)
    {
        _utf8 = utf8;
    }

    /// <summary>
    /// The documents, none for an empty text (or only comments).
    /// </summary>
    public ImmutableArray<YamlSyntax> Documents { get; private set; } = [];

    /// <exception cref="YamlSyntaxException">The text is not valid YAML.</exception>
    public static YamlStream Parse(string text) => Parse(Encoding.UTF8.GetBytes(text));

    /// <param name="utf8">The UTF-8 text, with or without a byte order mark.</param>
    /// <exception cref="YamlSyntaxException">The text is not valid YAML.</exception>
    public static YamlStream Parse(ReadOnlyMemory<byte> utf8)
    {
        if (utf8.Span.StartsWith("\uFEFF"u8))
        {
            utf8 = utf8[3..];
        }

        var stream = new YamlStream(utf8);
        var builder = new Builder(stream);
        var documents = ImmutableArray.CreateBuilder<YamlSyntax>();

        // Every scalar is a string token: the failsafe schema leaves the typing to the readers of the nodes.
        var reader = new Utf8YamlReader(utf8.Span, new YamlReaderOptions { AllowMultipleValues = true, Schema = YamlSchemaKind.Failsafe });

        try
        {
            while (reader.Read())
            {
                // Anchors belong to their document.
                builder.Anchors.Clear();
                documents.Add(builder.ReadNode(ref reader, 0));
            }
        }
        catch (YamlException e)
        {
            var line = (int)(e.LineNumber ?? 0);
            throw new YamlSyntaxException(line + 1, stream.Column(line, (int)(e.BytePositionInLine ?? 0)), ErrorMessage(e.Message));
        }

        stream.Documents = documents.DrainToImmutable();
        return stream;
    }

    /// <summary>
    /// The line and column (1 based, the column in UTF-16 code units) where <paramref name="node"/> starts.
    /// </summary>
    public (int Line, int Column) Position(YamlSyntax node) => PositionAt(node.Offset);

    // The line and column of a byte offset.
    private (int Line, int Column) PositionAt(int offset)
    {
        var starts = LineStarts();
        var index = Array.BinarySearch(starts, offset);
        var line = index >= 0 ? index : ~index - 1;
        return (line + 1, Column(line, offset - starts[line]));
    }

    // The column (1 based) of the byte at 'bytes' of a line (0 based).
    private int Column(int line, int bytes)
    {
        var starts = LineStarts();

        if (line >= starts.Length)
        {
            return bytes + 1;
        }

        var start = starts[line];
        var length = Math.Clamp(bytes, 0, _utf8.Length - start);
        return Encoding.UTF8.GetCharCount(_utf8.Span.Slice(start, length)) + 1;
    }

    private int[] LineStarts()
    {
        if (_lineStarts == null)
        {
            var span = _utf8.Span;
            var starts = new List<int> { 0 };
            var position = 0;

            while (span[position..].IndexOf((byte)'\n') is var index and >= 0)
            {
                position += index + 1;
                starts.Add(position);
            }

            _lineStarts = [.. starts];
        }

        return _lineStarts;
    }

    /// <summary>
    /// A Styml message without the position, which the location already gives, starting in lowercase like Douze's
    /// messages: <c>Unexpected end of input inside a YAML flow collection. LineNumber: 2 | BytePositionInLine: 0.</c>
    /// </summary>
    private static string ErrorMessage(string message)
    {
        message = PositionSuffix().Replace(message, "").Trim().TrimEnd('.');
        return message.Length == 0 ? "invalid YAML" : char.ToLowerInvariant(message[0]) + message[1..];
    }

    [GeneratedRegex(@"\s*(Path: \S*\s*\|\s*)?LineNumber: \d+ \| BytePositionInLine: \d+\.?")]
    private static partial Regex PositionSuffix();

    private sealed class Builder(YamlStream stream)
    {
        public Dictionary<string, YamlSyntax> Anchors { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Reads the node of the current token, and the tokens of its content.
        /// </summary>
        /// <param name="reader">The reader, on the first token of the node.</param>
        /// <param name="origin">The offset of the reader's first byte in the text: a key reader starts at its key.</param>
        public YamlSyntax ReadNode(ref Utf8YamlReader reader, int origin)
        {
            var offset = origin + (int)reader.TokenStartIndex;
            var anchor = reader.Anchor.IsEmpty ? null : Encoding.UTF8.GetString(reader.Anchor);
            YamlSyntax node;

            switch (reader.TokenType)
            {
                case YamlTokenType.StartObject:
                    var entries = ImmutableArray.CreateBuilder<KeyValuePair<YamlSyntax, YamlSyntax>>();

                    while (reader.Read() && reader.TokenType != YamlTokenType.EndObject)
                    {
                        var key = ReadKey(ref reader, origin);
                        reader.Read();
                        entries.Add(new(key, ReadNode(ref reader, origin)));
                    }

                    node = new YamlMapping(offset, entries.DrainToImmutable());
                    break;

                case YamlTokenType.StartArray:
                    var items = ImmutableArray.CreateBuilder<YamlSyntax>();

                    while (reader.Read() && reader.TokenType != YamlTokenType.EndArray)
                    {
                        items.Add(ReadNode(ref reader, origin));
                    }

                    node = new YamlSequence(offset, items.DrainToImmutable());
                    break;

                case YamlTokenType.Alias:
                    return Alias(Encoding.UTF8.GetString(reader.ValueSpan), offset);

                default:
                    node = new YamlScalar(offset, Text(ref reader), reader.ScalarStyle);
                    break;
            }

            if (anchor != null)
            {
                Anchors[anchor] = node;
            }

            return node;
        }

        private YamlSyntax ReadKey(ref Utf8YamlReader reader, int origin)
        {
            var offset = origin + (int)reader.TokenStartIndex;

            switch (reader.KeyKind)
            {
                case YamlKeyKind.Alias:
                    return Alias(Encoding.UTF8.GetString(reader.ValueSpan), offset);

                case YamlKeyKind.Complex:
                    // A collection used as a key: its own reader, whose offsets start at the key.
                    var keyReader = reader.CreateKeyReader();
                    keyReader.Read();
                    return ReadNode(ref keyReader, offset);

                default:
                    var key = new YamlScalar(offset, reader.GetString() ?? "", reader.ScalarStyle);

                    if (!reader.Anchor.IsEmpty)
                    {
                        Anchors[Encoding.UTF8.GetString(reader.Anchor)] = key;
                    }

                    return key;
            }
        }

        private YamlSyntax Alias(string name, int offset)
        {
            if (Anchors.TryGetValue(name, out var node))
            {
                return node;
            }

            // The reader does not resolve aliases.
            var (line, column) = stream.PositionAt(offset);
            throw new YamlSyntaxException(line, column, $"the alias '*{name}' has no anchor before it");
        }

        // A tagged scalar can be another token than a string (!!int '1'), whose text is the value as written.
        private string Text(ref Utf8YamlReader reader) => reader.TokenType is YamlTokenType.String
            ? reader.GetString() ?? ""
            : Encoding.UTF8.GetString(reader.ValueSpan);
    }
}

/// <summary>
/// A node of a <see cref="YamlStream"/>: a mapping, a sequence or a scalar.
/// </summary>
internal abstract class YamlSyntax(int offset)
{
    /// <summary>
    /// The UTF-8 offset of the node's first character in the text (<see cref="YamlStream.Position"/>).
    /// </summary>
    public int Offset { get; } = offset;
}

/// <summary>
/// A scalar: its text (quotes removed, escapes and block scalars resolved) and how it is written.
/// </summary>
internal sealed class YamlScalar(int offset, string value, YamlScalarStyle style) : YamlSyntax(offset)
{
    public string Value { get; } = value;

    public YamlScalarStyle Style { get; } = style;

    /// <summary>
    /// Whether the scalar is written without quotes and is not a block scalar: only a plain scalar stands for a
    /// number, a boolean or null.
    /// </summary>
    public bool IsPlain => Style is YamlScalarStyle.Plain;
}

/// <summary>
/// A mapping: its entries in document order, duplicate keys included.
/// </summary>
internal sealed class YamlMapping(int offset, ImmutableArray<KeyValuePair<YamlSyntax, YamlSyntax>> entries) : YamlSyntax(offset), IReadOnlyList<KeyValuePair<YamlSyntax, YamlSyntax>>
{
    public int Count => entries.Length;

    public KeyValuePair<YamlSyntax, YamlSyntax> this[int index] => entries[index];

    public ImmutableArray<KeyValuePair<YamlSyntax, YamlSyntax>>.Enumerator GetEnumerator() => entries.GetEnumerator();

    IEnumerator<KeyValuePair<YamlSyntax, YamlSyntax>> IEnumerable<KeyValuePair<YamlSyntax, YamlSyntax>>.GetEnumerator() => ((IEnumerable<KeyValuePair<YamlSyntax, YamlSyntax>>)entries).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)entries).GetEnumerator();
}

/// <summary>
/// A sequence: its items in document order.
/// </summary>
internal sealed class YamlSequence(int offset, ImmutableArray<YamlSyntax> items) : YamlSyntax(offset), IReadOnlyList<YamlSyntax>
{
    public int Count => items.Length;

    public YamlSyntax this[int index] => items[index];

    public ImmutableArray<YamlSyntax>.Enumerator GetEnumerator() => items.GetEnumerator();

    IEnumerator<YamlSyntax> IEnumerable<YamlSyntax>.GetEnumerator() => ((IEnumerable<YamlSyntax>)items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)items).GetEnumerator();
}

/// <summary>
/// A YAML text that does not parse, at a line and column (1 based) of the text. The message gives the position.
/// </summary>
internal sealed class YamlSyntaxException(int line, int column, string reason) : DouzeException($"line {line}, column {column}: {reason}")
{
    public int Line { get; } = line;

    public int Column { get; } = column;

    /// <summary>
    /// The error without its position.
    /// </summary>
    public string Reason { get; } = reason;
}
