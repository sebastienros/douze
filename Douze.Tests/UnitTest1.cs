using Douze.FileProcessors;
using System.Threading.Tasks;
using Xunit;
using Zio;
using Zio.FileSystems;

namespace Douze.Tests
{
    public class CommonTests
    {
        MemoryFileSystem _sourceFs;
        MemoryFileSystem _outoutFs;
        Options _options;

        public CommonTests()
        {
            _sourceFs = new MemoryFileSystem();
            _outoutFs = new MemoryFileSystem();

            _options = new Options(_sourceFs, _outoutFs);

            _options.ContentMappings[".md"] = new MarkdownFileProcessor();
            _options.ContentMappings[".html"] = new HtmlFileProcessor();
            _options.ContentMappings[".liquid"] = new HtmlFileProcessor();
            _options.DataMappings[".yml"] = new YamlDataFileProcessor();
            _options.DataMappings[".json"] = new JsonDataFileProcessor();
        }

        [Fact]
        public async Task ShouldProcessHtmlFile()
        {
            _sourceFs.WriteAllText("/index.html", "Hello World");
         
            await SiteHelper.BuildSiteAsync(_options);

            Assert.True(_outoutFs.FileExists("/index.html"));
            Assert.Equal("Hello World", _outoutFs.ReadAllText("/index.html"));
        }
    }
}