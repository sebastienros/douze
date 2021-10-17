using System.Text.Json.Nodes;

namespace Douze;

internal static class JsonHelper
{
    public static void Merge(JsonObject target, JsonObject node)
    {
        foreach (var property in node)
        {
            if (property.Value is JsonValue value)
            {
                target[property.Key] = Clone(value);
            }
            else if (property.Value is JsonArray array)
            {
                target[property.Key] = Clone(array);
            }
            else if (property.Value is JsonObject)
            {
                if (target.TryGetPropertyValue(property.Key, out var valueProperty))
                {
                    if (valueProperty is JsonObject jsonObject)
                    {
                        Merge(jsonObject, node);
                    }
                    else
                    {
                        target[property.Key] = Clone(valueProperty);
                    }
                }
            }
        }
    }

    public static JsonNode? Clone(JsonNode? node)
    {
        if (node == null)
        {
            return null;
        }

        return node switch
        {
            JsonValue => JsonValue.Create(node.AsValue().GetValue<object>()),
            JsonArray => new JsonArray(node.AsArray().Select(x => x == null ? null : Clone(x)).ToArray()),
            JsonObject => new JsonObject(node.AsObject().Select(x => new KeyValuePair<string, JsonNode?>(x.Key, Clone(x.Value)))),
            _ => throw new NotImplementedException()
        };
    }
}