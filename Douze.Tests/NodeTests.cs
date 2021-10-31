using System.Collections.Generic;
using Douze.Nodes;
using Xunit;

namespace Douze.Tests
{
    public class NodeTests
    {
        [Fact]
        public void NewNodeObjectShouldBeEmpty()
        {
            var node = new NodeObject();

            Assert.True(node.Count == 0);
        }

        [Fact]
        public void ShouldCreateNodeObjectFromKeyValuePairs()
        {
            var dic = new Dictionary<string, Node?>();
            dic.Add("a", NodeValue.Create(1));
            dic.Add("b", NodeValue.Create("string"));

            var node = new NodeObject(dic);

            Assert.True(node.Count == 2);
            Assert.True(node.ContainsKey("a"));
            Assert.True(node.ContainsKey("b"));
            Assert.Equal(1, node.GetItem("a")?.GetValue<int>());
            Assert.Equal("string", node.GetItem("b")?.GetValue<string>());
        }

        [Fact]
        public void PropertieShouldBeAddedToNodeObject()
        {
            var node = new NodeObject();

            node.Add("a", 1);
            node.Add("b", "string");

            Assert.True(node.Count == 2);
            Assert.True(node.ContainsKey("a"));
            Assert.True(node.ContainsKey("b"));
            Assert.Equal(1, node.GetItem("a")?.GetValue<int>());
            Assert.Equal("string", node.GetItem("b")?.GetValue<string>());
        }

        [Fact]
        public void NodeObjectShouldContainNodeObject()
        {
            var node1 = new NodeObject();
            var node2 = new NodeObject();

            node1.Add("a", node2);
            node2.Add("b", "string");

            Assert.True(node1.Count == 1);
            Assert.True(node2.Count == 1);
            Assert.True(node1.ContainsKey("a"));
            Assert.True(node2.ContainsKey("b"));
            Assert.Same(node2, node1["a"]);
            Assert.Equal("string", node1["a"]?["b"]?.GetValue<string>());
        }

        [Fact]
        public void NodeObjectShouldCanBeShared()
        {
            var node1 = new NodeObject();
            var node2 = new NodeObject();
            var node3 = new NodeObject();

            node1.Add("a", node3);
            node2.Add("a", node3);
            node3.Add("b", "string");

            Assert.True(node1.Count == 1);
            Assert.True(node2.Count == 1);
            Assert.True(node1.ContainsKey("a"));
            Assert.True(node2.ContainsKey("a"));
            Assert.Same(node3, node1["a"]);
            Assert.Same(node3, node2["a"]);
        }

        [Fact]
        public void PrimitiveTypesShouldBeConvertedToNodeValues()
        {
            var n = new NodeObject();
            n.Add("a", 1);
            n.Add("b", true);

            Assert.True(n.Count == 2);
            Assert.Equal(1, n["a"]?.GetValue<int>());
            Assert.Equal(true, n["b"]?.GetValue<bool>());

            Assert.Equal(1, n["a"]?.GetValue<object>());
            Assert.Equal(true, n["b"]?.GetValue<object>());
        }

        [Fact]
        public void ShouldCloneNodes()
        {
            var n = new NodeObject
            {
                ["a"] = 1,
                ["b"] = true,
                ["c"] = new NodeArray(2, "string", null),
                ["d"] = null
            };

            var clone = n.Clone().AsObject();

            Assert.Equal(4, clone.Count);
            Assert.Equal(1, clone["a"]?.GetValue<int>());
            Assert.Equal(true, clone["b"]?.GetValue<bool>());

            Assert.Equal(3, n["c"]?.AsArray().Count);
            Assert.Equal(2, n["c"]?.AsArray()[0]?.GetValue<int>());
            Assert.Equal("string", n["c"]?.AsArray()[1]?.GetValue<string>());
            Assert.Null(n["c"]?.AsArray()[2]);
        }

        [Fact]
        public void ShouldMergeNodeObjects()
        {
            var n1 = new NodeObject
            {
                ["a"] = 1,
                ["b"] = true,
                ["c"] = new NodeArray(2, "string", null),
                ["d"] = null
            };

            var n2 = new NodeObject
            {
                ["a"] = 1,
                ["c"] = new NodeArray("foo", 2, 1),
                ["d"] = null,
                ["e"] = 3
            };

            n1.Merge(n2);

            Assert.Equal(5, n1.Count);
            Assert.Equal(1, n1["a"]?.GetValue<int>());
            Assert.Equal(true, n1["b"]?.GetValue<bool>());
            Assert.Equal(5, n1["c"]?.AsArray().Count);
            Assert.Null(n1["d"]);
            Assert.Equal(3, n1["e"]?.GetValue<int>());

        }
    }
}
