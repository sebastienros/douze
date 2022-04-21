using System.Text.Json.Nodes;
using System.Xml.Linq;
using Douze.Nodes;

namespace Douze;

internal static class XmlHelper
{
    public static Node? Convert(XObject? xmlNode)
    {
        if (xmlNode == null)
        {
            return null;
        }

        if (xmlNode is XDocument document)
        {
            if (document.Root == null)
            {
                return null;
            }

            var result = new NodeObject();

            result[document.Root.Name.LocalName] = Convert(document.Root);

            return result;
        }
        else if (xmlNode is XElement element)
        {
            var result = new NodeObject();

            var attributes = new NodeObject();
            var elements = new NodeArray();
            result["name"] = element.Name.LocalName;
            result["attributes"] = attributes;
            result["elements"] = elements;

            foreach (var item in element.Attributes())
            {
                attributes.Add(item.Name.LocalName, item.Value);
            }

            foreach (var item in element.Elements())
            {
                elements.Add(Convert(item));
            }

            if (!element.HasElements && element.FirstNode is XText textNode)
            {
                result["text"] = textNode.Value;
            }

            return result;
        }

        return null;
    }
}
