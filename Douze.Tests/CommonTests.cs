using Douze.FileProcessors;
using Douze.FileSystems;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

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
            await _sourceFs.WriteTextFileAsync("/index.html", "Hello World");

            await SiteHelper.BuildSiteAsync(_options);

            Assert.True((await _outputFs.GetFileEntryAsync("/index.html")).Exists);
            Assert.Equal("Hello World", await _outputFs.ReadAllTextAsync("/index.html"));
        }

        [Fact]
        public async Task ShouldRenderLayout()
        {
            await _sourceFs.CreateTemplateAsync("/index.html", "Hello World", new { layout = "layout.html" });
            await _sourceFs.CreateTemplateAsync("/_includes/layout.html", "BEFFORE {{ content }} AFTER");

            await SiteHelper.BuildSiteAsync(_options);

            // 1 File and 1 Directory entries
            Assert.Equal(2, (await _outputFs.EnumerateFileSystemEntriesAsync(FPath.Root, true, null)).Count());

            Assert.True((await _outputFs.GetFileEntryAsync("/index.html")).Exists);
            Assert.Equal("BEFFORE Hello World AFTER", await _outputFs.ReadAllTextAsync("/index.html"));
        }

        [Fact]
        public async Task ShouldLoadDataFiles()
        {
            await _sourceFs.CreateTemplateAsync("/index.html", "Hello World");
            await _sourceFs.WriteTextFileAsync("/_data/theme.yml", "layout: layout.html");
            await _sourceFs.CreateTemplateAsync("/_includes/layout.html", "BEFFORE {{ content }} AFTER");

            await SiteHelper.BuildSiteAsync(_options);

            // 1 File and 1 Directory entries
            Assert.Equal(2, (await _outputFs.EnumerateFileSystemEntriesAsync(FPath.Root, true, null)).Count());

            Assert.True((await _outputFs.GetFileEntryAsync("/index.html")).Exists);
            Assert.Equal("BEFFORE Hello World AFTER", await _outputFs.ReadAllTextAsync("/index.html"));
        }

        [Fact]
        public async Task ShouldRenderBaseLayout()
        {
            await _sourceFs.CreateTemplateAsync("/index.html", "Hello World", new { layout = "layout.html" });
            await _sourceFs.CreateTemplateAsync("/_includes/layout.html", "BEFFORE {{ content }} AFTER", new { layout = "master.html" });
            await _sourceFs.CreateTemplateAsync("/_includes/master.html", "BEGIN {{ content }} END");

            await SiteHelper.BuildSiteAsync(_options);

            Assert.True((await _outputFs.GetFileEntryAsync("/index.html")).Exists);
            Assert.Equal("BEGIN BEFFORE Hello World AFTER END", await _outputFs.ReadAllTextAsync("/index.html"));
        }
    }
}
