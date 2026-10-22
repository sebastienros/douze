using System.Globalization;
using System.Text.Json;
using Douze.Nodes;
using Douze.Workflow;

namespace Douze.Liquid.Filters;

/// <summary>
/// Writes a node as JSON in its own order (the <c>json</c> filter); <see cref="CanonicalJson"/> sorts keys for hashes.
/// </summary>
internal static class NodeJson
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

                foreach (var property in obj)
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

            case NodeValue<string> s:
                writer.WriteStringValue(s.Value);
                break;

            case NodeValue<bool> b:
                writer.WriteBooleanValue(b.Value);
                break;

            case NodeValue<int> i:
                writer.WriteNumberValue(i.Value);
                break;

            case NodeValue<long> l:
                writer.WriteNumberValue(l.Value);
                break;

            case NodeValue<double> d:
                writer.WriteNumberValue(d.Value);
                break;

            case NodeValue<decimal> m:
                writer.WriteNumberValue(m.Value);
                break;

            case NodeValue<Metadata> metadata:
                Write(writer, metadata.Value.Own);
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
