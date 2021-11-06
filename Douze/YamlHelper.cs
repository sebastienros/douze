using Douze.Nodes;
using SharpYaml.Model;

namespace Douze;

internal sealed class YamlHelper
{
    public static Node? Parse(string content)
    {
        using var reader = new StringReader(content);
        return Parse(reader);
    }

    public static Node? Parse(TextReader reader)
    {
        var stream = YamlStream.Load(reader);

        return Convert(stream[0].Contents);
    }

    /// <summary>
    /// Transform any Yaml node into a CLR object
    /// </summary>
    /// <param name="node"></param>
    /// <returns></returns>
    private static Node? Convert(YamlNode node)
    {
        if (node is YamlMapping yamlMapping)
        {
            var dictionary = new NodeObject();

            foreach (var child in yamlMapping)
            {
                dictionary[child.Key.ToString()] = Convert(child.Value);
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
            var array = new NodeArray();

            for (var i = 0; i < length; i++)
            {
                array.Add(Convert(yamlSequence[i]));
            }

            return array;
        }

        return null;
    }
}
