using System.Text.Json;
using System.Text.Json.Nodes;
using Douze.Nodes;

namespace Douze;

internal static class JsonHelper
{
    public static Node? Convert(JsonNode? jsonNode)
    {
        if (jsonNode == null)
        {
            return null;
        }

        if (jsonNode is JsonValue value)
        {
            var underlyingValue = value.GetValue<object>();

            if (underlyingValue is JsonElement element)
            {
                return element.ValueKind switch
                {
                    JsonValueKind.String => NodeValue.Create(element.GetString()),
                    JsonValueKind.Number => NodeValue.Create(element.GetDecimal()),
                    JsonValueKind.False => NodeValue.Create(false),
                    JsonValueKind.True => NodeValue.Create(true),
                    _ => null
                };
            }
            else
            {
                return NodeValue.Create(underlyingValue);
            }
        }
        else if (jsonNode is JsonArray array)
        {
            return new NodeArray(array.Select(a => Convert(a)).ToArray());
        }
        else if (jsonNode is JsonObject obj)
        {
            var result = new NodeObject();

            foreach (var item in obj)
            {
                result.Add(item.Key, Convert(item.Value));
            }

            return result;
        }

        return null;
    }
}
