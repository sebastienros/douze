//using System.Text.Json.Nodes;
//using YamlDotNet.RepresentationModel;

//namespace Douze
//{
//    internal class YamlHelper
//    {
//        public static JsonObject Parse(string content)
//        {
//            using var reader = new StringReader(content);
//            var data = new JsonObject();

//            MergeData(data, reader);

//            return data;
//        }

//        public static void MergeData(JsonObject data, TextReader reader)
//        {
//            var yaml = new YamlStream();
//            yaml.Load(reader);

//            MergeNode(data, "", yaml.Documents[0].RootNode);
//        }

//        /// <summary>
//        /// Process the nodes recursively
//        /// </summary>
//        /// <param name="data"></param>
//        /// <param name="name"></param>
//        /// <param name="node"></param>
//        private static void MergeNode(JsonObject data, string name, YamlNode node)
//        {
//            switch (node.NodeType)
//            {
//                case YamlNodeType.Alias: break;
//                case YamlNodeType.Mapping: 
//                    var mappingNode = (YamlMappingNode)node;

//                    foreach (var child in mappingNode.Children)
//                    {
//                        MergeNode(data, child.Key.ToString(), child.Value);
//                    }

//                    break;
//                case YamlNodeType.Scalar: 

//                    data[name] = MaterializeNode(node);
//                    break;
//                case YamlNodeType.Sequence: 

//                    data[name] = MaterializeNode(node);
//                    break;
//            }
//        }

//        /// <summary>
//        /// Transform any Yaml node into a CLR object
//        /// </summary>
//        /// <param name="node"></param>
//        /// <returns></returns>
//        private static JsonNode? MaterializeNode(YamlNode node)
//        {
//            if (node == null)
//            {
//                return null;
//            }

//            switch (node.NodeType)
//            {
//                case YamlNodeType.Alias:
//                    return null;

//                case YamlNodeType.Mapping:
//                    var mappingNode = (YamlMappingNode)node;
//                    var dictionary = new JsonObject();

//                    foreach (var child in mappingNode.Children)
//                    {
//                        dictionary[child.Key.ToString()] = MaterializeNode(child.Value);
//                    }

//                    return dictionary;

//                case YamlNodeType.Scalar:
//                    var scalarNode = (YamlScalarNode)node;

//                    return scalarNode.Value;
                
//                case YamlNodeType.Sequence:
//                    var sequenceNode = (YamlSequenceNode)node;

//                    var length = sequenceNode.Children.Count;
//                    var array = new JsonArray();

//                    for (var i = 0; i < length; i++)
//                    {
//                        array.Add(MaterializeNode(sequenceNode.Children[i]));
//                    }

//                    return array;
//            }

//            return null;
//        }
//    }
//}
