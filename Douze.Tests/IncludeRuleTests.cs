using Douze.Configuration;
using Xunit;

namespace Douze.Tests
{
    public class IncludeRuleTests
    {
        [Fact]
        public void ShouldMatchFileNamesAtRoot()
        {
            // A leading slash matches the beginning of the pathname
            var match = IncludeRule.ParseRules("/config.json");

            Assert.True(match("/config.json"));
            Assert.False(match("/a/config.json"));
            Assert.True(match("config.json"));
            Assert.False(match("/data.json"));
        }

        [Fact]
        public void ShouldMatchFileNamesInAnyDir()
        {
            // a file config.json anywhere in the hierarchy
            var match = IncludeRule.ParseRules("config.json");

            Assert.True(match("/config.json"));
            Assert.True(match("config.json"));
            Assert.True(match("/a/config.json"));
            Assert.False(match("data.json"));
            Assert.False(match("aconfig.json"));
        }

        [Fact]
        public void ShouldMatchAnyFileWithExtension()
        {
            var match = IncludeRule.ParseRules("*.json");

            Assert.True(match("/config.json"));
            Assert.True(match("config.json"));
            Assert.True(match("/a/config.json"));
            Assert.True(match("a/config.json"));
            Assert.False(match("a.foo"));
            Assert.False(match("a.json2"));
        }

        [Fact]
        public void ShouldMatchFileWithExtensionAtRoot()
        {
            var match = IncludeRule.ParseRules("/*.json");

            Assert.True(match("/config.json"));
            Assert.True(match("config.json"));
            Assert.False(match("/a/config.json"));
            Assert.False(match("a/config.json"));
            Assert.False(match("a.foo"));
            Assert.False(match("a.json2"));
        }

        [Fact]
        public void ShouldMatchFileWithExtensionInSubDirs()
        {
            var match = IncludeRule.ParseRules("**/*.json");

            Assert.True(match("/config.json"));
            Assert.True(match("config.json"));
            Assert.True(match("/a/config.json"));
            Assert.True(match("a/config.json"));
            Assert.False(match("a.foo"));
            Assert.False(match("a.json2"));
        }
    }
}
