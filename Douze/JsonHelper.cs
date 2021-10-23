using System.Diagnostics.CodeAnalysis;
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
            // Doesn't work with trimming
            //JsonValue.Create(node.AsValue().GetValue<object>()),

            JsonValue => CloneJsValue(node.AsValue()),
            JsonArray => new JsonArray(node.AsArray().Select(x => x == null ? null : Clone(x)).ToArray()),
            JsonObject => new JsonObject(node.AsObject().Select(x => new KeyValuePair<string, JsonNode?>(x.Key, Clone(x.Value)))),
            _ => throw new NotImplementedException()
        };

        static JsonValue? CloneJsValue(JsonValue value)
        {
            if (value.TryGetValue<string>(out var s))
            {
                return JsonValue.Create(s);
            }

            if (value.TryGetValue<bool>(out var b))
            {
                return JsonValue.Create(b);
            }

            if (value.TryGetValue<DateTime>(out var dt))
            {
                return JsonValue.Create(dt);
            }

            if (value.TryGetValue<decimal>(out var d))
            {
                return JsonValue.Create(d);
            }

            return JsonValue.Create(value.GetValue<object>().ToString());
        }
    }
}
