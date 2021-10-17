using SharpYaml.Model;
using System.Text.Json.Nodes;

namespace Douze;

internal sealed class YamlHelper
{
    public static JsonObject Parse(string content)
    {
        using var reader = new StringReader(content);
        var data = new JsonObject();

        MergeData(data, reader);

        return data;
    }

    public static void MergeData(JsonObject data, TextReader reader)
    {
        var stream = YamlStream.Load(reader);

        MergeNode(data, "", stream[0].Contents);
    }

    /// <summary>
    /// Process the nodes recursively
    /// </summary>
    /// <param name="data"></param>
    /// <param name="name"></param>
    /// <param name="node"></param>
    private static void MergeNode(JsonObject data, string name, YamlNode node)
    {
        if (node is YamlMapping yamlMapping)
        {
            foreach (var child in yamlMapping)
            {
                MergeNode(data, child.Key.ToString(), child.Value);
            }
        }
        else if (node is YamlValue yamlValue)
        {
            data[name] = MaterializeNode(yamlValue);
        }
        else if (node is YamlSequence yamlSequence)
        {
            data[name] = MaterializeNode(yamlSequence);
        }
    }

    /// <summary>
    /// Transform any Yaml node into a CLR object
    /// </summary>
    /// <param name="node"></param>
    /// <returns></returns>
    private static JsonNode? MaterializeNode(YamlNode node)
    {
        if (node is YamlMapping yamlMapping)
        {
            var dictionary = new JsonObject();

            foreach (var child in yamlMapping)
            {
                dictionary[child.Key.ToString()] = MaterializeNode(child.Value);
            }

            return dictionary;
        }
        else if (node is YamlValue yamlValue)
        {
            return yamlValue.Value;
        }
        else if (node is YamlSequence yamlSequence)
        {
            var length = yamlSequence.Count;
            var array = new JsonArray();

            for (var i = 0; i < length; i++)
            {
                array.Add(MaterializeNode(yamlSequence[i]));
            }

            return array;
        }

        return null;
    }
}
