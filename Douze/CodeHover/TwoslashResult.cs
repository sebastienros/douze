using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Douze.CodeHover;

internal enum TwoslashNodeType { Completion, Error, Highlight, Hover, Query, Tag }

/// <summary>
/// A completion entry of a <c>^|</c> query.
/// </summary>
internal sealed record CompletionEntry(string Name, string Kind, string? KindModifiers, string? SortText);

/// <summary>
/// A node of a twoslash result, with the fields of twoslash's protocol (<c>twoslash-protocol</c>): a hover or a quick
/// info query (<see cref="Text"/>, <see cref="Docs"/>, <see cref="Tags"/>, <see cref="Target"/>), an error
/// (<see cref="Code"/>, <see cref="Text"/>, <see cref="Level"/>, <see cref="Filename"/>), a custom tag
/// (<see cref="Name"/>, <see cref="Text"/>), a highlight or a completion. Offsets are UTF-16 code units in the code,
/// like JavaScript strings. An error of a language whose diagnostic codes are not numbers (<c>CS1061</c>) has its code
/// in <see cref="CodeName"/>, written as a string.
/// </summary>
internal sealed class TwoslashNode(TwoslashNodeType type, int start, int length)
{
    public TwoslashNodeType Type { get; } = type;
    public int Start { get; set; } = start;
    public int Length { get; } = length;
    public int Line { get; set; }
    public int Character { get; set; }
    public string? Text { get; init; }
    public string? Docs { get; init; }
    public IReadOnlyList<(string Name, string? Text)>? Tags { get; init; }
    public string? Target { get; init; }
    public int Code { get; init; }
    public string? CodeName { get; init; }
    public string? Filename { get; init; }
    public string? Level { get; init; }
    public string? Name { get; init; }
    public IReadOnlyList<CompletionEntry>? Completions { get; init; }
    public string? CompletionsPrefix { get; init; }

    public string TypeName => Type switch
    {
        TwoslashNodeType.Completion => "completion",
        TwoslashNodeType.Error => "error",
        TwoslashNodeType.Highlight => "highlight",
        TwoslashNodeType.Hover => "hover",
        TwoslashNodeType.Query => "query",
        _ => "tag",
    };
}

/// <summary>
/// What twoslash returns for a snippet: the code without its notations, and the nodes, sorted by offset then type,
/// with their line and character in that code. <see cref="ToJson"/> writes the shape of twoslash's
/// <c>{ code, nodes }</c>, which a highlighter can use in place of running twoslash. Every code step writes it, whatever
/// the language, so that one renderer shows the hovers and errors of all of them.
/// </summary>
internal sealed class TwoslashResult(string code, IReadOnlyList<TwoslashNode> nodes)
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The result of <paramref name="nodes"/> in <paramref name="code"/>: those with a position (a negative
    /// <see cref="TwoslashNode.Start"/> drops a node) sorted by offset then type, with their line and character
    /// (twoslash's <c>resolveNodePositions</c>).
    /// </summary>
    public static TwoslashResult Resolve(string code, IEnumerable<TwoslashNode> nodes)
    {
        var positions = new PositionConverter(code);
        var resolved = nodes.Where(x => x.Start >= 0).OrderBy(x => x.Start).ThenBy(x => x.TypeName, StringComparer.Ordinal).ToList();

        foreach (var node in resolved)
        {
            (node.Line, node.Character) = positions.ToPosition(node.Start);
        }

        return new TwoslashResult(code, resolved);
    }

    public string Code { get; } = code;

    public IReadOnlyList<TwoslashNode> Nodes { get; } = nodes;

    public string ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("code", Code);
            writer.WriteStartArray("nodes");

            foreach (var node in Nodes)
            {
                WriteNode(writer, node);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteNode(Utf8JsonWriter writer, TwoslashNode node)
    {
        writer.WriteStartObject();
        writer.WriteString("type", node.TypeName);

        if (node.Type == TwoslashNodeType.Tag)
        {
            writer.WriteString("name", node.Name);
        }

        writer.WriteNumber("start", node.Start);
        writer.WriteNumber("length", node.Length);
        writer.WriteNumber("line", node.Line);
        writer.WriteNumber("character", node.Character);

        switch (node.Type)
        {
            case TwoslashNodeType.Hover or TwoslashNodeType.Query:
                writer.WriteString("text", node.Text);

                if (node.Docs != null)
                {
                    writer.WriteString("docs", node.Docs);
                }

                if (node.Tags != null)
                {
                    writer.WriteStartArray("tags");

                    foreach (var (name, text) in node.Tags)
                    {
                        writer.WriteStartArray();
                        writer.WriteStringValue(name);

                        if (text != null)
                        {
                            writer.WriteStringValue(text);
                        }

                        writer.WriteEndArray();
                    }

                    writer.WriteEndArray();
                }

                writer.WriteString("target", node.Target);
                break;

            case TwoslashNodeType.Error:
                if (node.CodeName != null)
                {
                    writer.WriteString("code", node.CodeName);
                }
                else
                {
                    writer.WriteNumber("code", node.Code);
                }

                writer.WriteString("filename", node.Filename);
                writer.WriteString("id", $"err-{node.CodeName ?? node.Code.ToString(CultureInfo.InvariantCulture)}-{node.Start}-{node.Length}");
                writer.WriteString("text", node.Text);
                writer.WriteString("level", node.Level);
                break;

            case TwoslashNodeType.Completion:
                writer.WriteStartArray("completions");

                foreach (var entry in node.Completions ?? [])
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", entry.Name);
                    writer.WriteString("kind", entry.Kind);

                    if (entry.KindModifiers != null)
                    {
                        writer.WriteString("kindModifiers", entry.KindModifiers);
                    }

                    if (entry.SortText != null)
                    {
                        writer.WriteString("sortText", entry.SortText);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteString("completionsPrefix", node.CompletionsPrefix);
                break;

            default:
                if (node.Text != null)
                {
                    writer.WriteString("text", node.Text);
                }

                break;
        }

        writer.WriteEndObject();
    }
}
