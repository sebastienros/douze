using Douze.FileProcessors;
using System.Threading.Tasks;
using Xunit;
using Zio;
using Zio.FileSystems;

namespace Douze.Tests
{
    public class CommonTests
    {
        readonly MemoryFileSystem _sourceFs;
        readonly MemoryFileSystem _outputFs;
        readonly Options _options;

        public CommonTests()
        {
            _sourceFs = new MemoryFileSystem();
            _outputFs = new MemoryFileSystem();

            _options = new Options(_sourceFs, _outputFs);

            _options.ContentMappings[".md"] = new MarkdownFileProcessor();
            _options.ContentMappings[".html"] = new HtmlFileProcessor();
            _options.ContentMappings[".liquid"] = new HtmlFileProcessor();
            _options.DataMappings[".yml"] = new YamlDataFileProcessor();
            _options.DataMappings[".json"] = new JsonDataFileProcessor();
        }

        [Fact]
        public async Task ShouldRenderHtmlFile()
        {
            _sourceFs.WriteAllText("/index.html", "Hello World");

            await SiteHelper.BuildSiteAsync(_options);

            Assert.True(_outputFs.FileExists("/index.html"));
            Assert.Equal("Hello World", _outputFs.ReadAllText("/index.html"));
        }

        [Fact]
        public async Task ShouldRenderLayout()
        {
            _sourceFs.CreateTemplate("/index.html", "Hello World", new { layout = "layout.html" });
            _sourceFs.CreateTemplate("/_includes/layout.html", "BEFFORE {{ content }} AFTER");

            await SiteHelper.BuildSiteAsync(_options);

            Assert.True(_outputFs.FileExists("/index.html"));
            Assert.Equal("BEFFORE Hello World AFTER", _outputFs.ReadAllText("/index.html"));
        }

        [Fact]
        public async Task ShouldRenderBaseLayout()
        {
            _sourceFs.CreateTemplate("/index.html", "Hello World", new { layout = "layout.html" });
            _sourceFs.CreateTemplate("/_includes/layout.html", "BEFFORE {{ content }} AFTER", new { layout = "master.html" });
            _sourceFs.CreateTemplate("/_includes/master.html", "BEGIN {{ content }} END");

            await SiteHelper.BuildSiteAsync(_options);

            Assert.True(_outputFs.FileExists("/index.html"));
            Assert.Equal("BEGIN BEFFORE Hello World AFTER END", _outputFs.ReadAllText("/index.html"));
        }
    }
}
