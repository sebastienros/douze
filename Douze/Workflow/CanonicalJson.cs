using System.Globalization;
using System.Text.Json;
using Douze.Nodes;

namespace Douze.Workflow;

/// <summary>
/// Writes nodes as canonical JSON (keys sorted ordinally, invariant formatting), for configuration hashes and the
/// metadata given to commands. Uses <see cref="Utf8JsonWriter"/> directly, so no reflection is involved.
/// </summary>
internal static class CanonicalJson
{
    public static void Write(Utf8JsonWriter writer, Node? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;

            case NodeObject obj:
                writer.WriteStartObject();

                var properties = obj.ToArray();
                Array.Sort(properties, static (a, b) => string.CompareOrdinal(a.Key, b.Key));

                foreach (var property in properties)
                {
                    writer.WritePropertyName(property.Key);
                    Write(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case NodeArray array:
                writer.WriteStartArray();

                foreach (var item in array)
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;

            default:
                WriteValue(writer, node);
                break;
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, Node node)
    {
        switch (node)
        {
            case NodeValue<string> s: writer.WriteStringValue(s.Value); break;
            case NodeValue<bool> b: writer.WriteBooleanValue(b.Value); break;
            case NodeValue<int> i: writer.WriteNumberValue(i.Value); break;
            case NodeValue<long> l: writer.WriteNumberValue(l.Value); break;
            case NodeValue<decimal> m: writer.WriteNumberValue(m.Value); break;
            case NodeValue<double> d when double.IsFinite(d.Value): writer.WriteNumberValue(d.Value); break;
            case NodeValue<float> f when float.IsFinite(f.Value): writer.WriteNumberValue(f.Value); break;
            case NodeValue<DateTime> dt: writer.WriteStringValue(dt.Value.ToString("O", CultureInfo.InvariantCulture)); break;
            case NodeValue<DateTimeOffset> dto: writer.WriteStringValue(dto.Value.ToString("O", CultureInfo.InvariantCulture)); break;

            // Metadata wrapped in a node (collection items) is identified by its own hash.
            case NodeValue<Metadata> meta:
                writer.WriteStartObject();
                writer.WriteString("$metadata", meta.Value.Hash.ToString());
                writer.WriteEndObject();
                break;

            case IFormattable formattable:
                writer.WriteStringValue(formattable.ToString(null, CultureInfo.InvariantCulture));
                break;

            default:
                writer.WriteStringValue(node.ToString());
                break;
        }
    }
}
