using Douze.Configuration;
using Douze.FileProcessors;
using Douze.FileSystems;
using System;
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

            _options.ContentProcessors.Add(new PageMetadataProcessor());
            _options.ContentProcessors.Add(new FrontMatterProcessor(IncludeRule.ParseRules("*.md", "*.html", "*.liquid")));
            _options.ContentProcessors.Add(new MarkdownFileProcessor(IncludeRule.ParseRules("*.md")));
            _options.ContentProcessors.Add(new UrlFileProcessor(IncludeRule.ParseRules("*.md", "*.html")));
            _options.ContentProcessors.Add(new CollectionsProcessor(IncludeRule.ParseRules("*.html")));
            _options.ContentProcessors.Add(new LiquidTemplateFileProcessor(IncludeRule.ParseRules("*.html", "*.json")));
            _options.ContentProcessors.Add(new LayoutFileProcessor(IncludeRule.ParseRules("*.html")));
            _options.ContentProcessors.Add(new CopyDocumentProcessor(IncludeRule.ParseRules("*.jpg", "*.gif", "*.png")));

            _options.DataProcessors.Add(new YamlDataFileProcessor());
            _options.DataProcessors.Add(new JsonDataFileProcessor());
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
        public async Task ShouldRenderMarkdownFile()
        {
            await _sourceFs.WriteTextFileAsync("/article.md", "**Hello World**");

            await SiteHelper.BuildSiteAsync(_options);

            Assert.True((await _outputFs.GetFileEntryAsync("/article/index.html")).Exists);
            Assert.Equal("<p><strong>Hello World</strong></p>\n", await _outputFs.ReadAllTextAsync("/article/index.html"));
        }

        [Fact]
        public async Task ShouldGenerateFolderWithIndex()
        {
            await _sourceFs.WriteTextFileAsync("/index.md", "**Hello World**");

            await SiteHelper.BuildSiteAsync(_options);

            Assert.True((await _outputFs.GetFileEntryAsync("/index.html")).Exists);
            Assert.Equal("<p><strong>Hello World</strong></p>\n", await _outputFs.ReadAllTextAsync("/index.html"));
        }

        [Fact]
        public async Task ShouldRenderLayout()
        {
            await _sourceFs.CreateTemplateAsync("/index.html", "Hello World", new { layout = "layout.html" });
            await _sourceFs.CreateTemplateAsync("/_includes/layout.html", "BEFORE {{ content }} AFTER");

            await SiteHelper.BuildSiteAsync(_options);

            // 1 File and 1 Directory entries
            Assert.Equal(2, (await _outputFs.EnumerateFileSystemEntriesAsync(FPath.Root, true, null)).Count());

            Assert.True((await _outputFs.GetFileEntryAsync("/index.html")).Exists);
            Assert.Equal("BEFORE Hello World AFTER", await _outputFs.ReadAllTextAsync("/index.html"));
        }

        [Fact]
        public async Task ShouldLoadDataFiles()
        {
            await _sourceFs.CreateTemplateAsync("/index.html", "Hello World");
            await _sourceFs.WriteTextFileAsync("/_data/theme.yml", "layout: layout.html");
            await _sourceFs.CreateTemplateAsync("/_includes/layout.html", "BEFORE {{ content }} AFTER");

            await SiteHelper.BuildSiteAsync(_options);

            // 1 File and 1 Directory entries
            Assert.Equal(2, (await _outputFs.EnumerateFileSystemEntriesAsync(FPath.Root, true, null)).Count());

            Assert.True((await _outputFs.GetFileEntryAsync("/index.html")).Exists);
            Assert.Equal("BEFORE Hello World AFTER", await _outputFs.ReadAllTextAsync("/index.html"));
        }

        [Fact]
        public async Task ShouldRenderBaseLayout()
        {
            await _sourceFs.CreateTemplateAsync("/index.html", "Hello World", new { layout = "layout.html" });
            await _sourceFs.CreateTemplateAsync("/_includes/layout.html", "BEFORE {{ content }} AFTER", new { layout = "master.html" });
            await _sourceFs.CreateTemplateAsync("/_includes/master.html", "BEGIN {{ content }} END");

            await SiteHelper.BuildSiteAsync(_options);

            Assert.True((await _outputFs.GetFileEntryAsync("/index.html")).Exists);
            Assert.Equal("BEGIN BEFORE Hello World AFTER END", await _outputFs.ReadAllTextAsync("/index.html"));
        }

        [Fact]
        public async Task PermalinkShouldChangeFilename()
        {
            await _sourceFs.CreateTemplateAsync("/index.html", "Hello World", new { permalink = "posts/my-post" });

            await SiteHelper.BuildSiteAsync(_options);

            Assert.True((await _outputFs.GetFileEntryAsync("/posts/my-post/index.html")).Exists);
            Assert.Equal("Hello World", await _outputFs.ReadAllTextAsync("/posts/my-post/index.html"));
        }

        [Fact]
        public async Task CustomFileNameShouldUseFolders()
        {
            await _sourceFs.CreateTemplateAsync("/articles/article.md", "Hello World");

            await SiteHelper.BuildSiteAsync(_options);

            Assert.True((await _outputFs.GetFileEntryAsync("/articles/article/index.html")).Exists);
        }

        [Fact]
        public async Task PermalinkShouldBeTemplated()
        {
            await _sourceFs.CreateTemplateAsync("/index.html", "Hello World", new { title = "My Blog Post", permalink = "posts//{{ title | slug }}/index.html" });

            await SiteHelper.BuildSiteAsync(_options);

            Assert.True((await _outputFs.GetFileEntryAsync("/posts/my-blog-post/index.html")).Exists);
            Assert.Equal("Hello World", await _outputFs.ReadAllTextAsync("/posts/my-blog-post/index.html"));
        }

        [Fact]
        public async Task PermalinkShouldPreserveCustomFilenames()
        {
            await _sourceFs.CreateTemplateAsync("/api.html", "{ \"error\": false }", new { permalink = "posts/api.json" });

            await SiteHelper.BuildSiteAsync(_options);

            Assert.True((await _outputFs.GetFileEntryAsync("posts/api.json")).Exists);
            Assert.Equal("{ \"error\": false }", await _outputFs.ReadAllTextAsync("/posts/api.json"));
        }

        [Fact]
        public async Task TagsShouldBeAvailableInCollections()
        {
            await _sourceFs.CreateTemplateAsync("/articles/article1.md", "Hello World", new { tags = new[] { "article", "science" } });
            await _sourceFs.CreateTemplateAsync("/articles/article2.md", "Hello World", new { tags = new[] { "article", "music" } });
            await _sourceFs.CreateTemplateAsync("/articles/article3.md", "Hello World", new { tags = new[] { "article", "music", "science" } });
            await _sourceFs.CreateTemplateAsync("/articles/article4.md", "Hello World", new { tags = "article" });
            await _sourceFs.CreateTemplateAsync("/blog/post1.md", "Hello World");

            await SiteHelper.BuildSiteAsync(_options);

            Assert.NotNull(_options.GlobalData["collections"]);

            Assert.Equal(5, _options.GlobalData["collections"]?["all"]?.AsArray()?.Count);
            Assert.Equal(4, _options.GlobalData["collections"]?["article"]?.AsArray()?.Count);
            Assert.Equal(2, _options.GlobalData["collections"]?["music"]?.AsArray()?.Count);
            Assert.Equal(2, _options.GlobalData["collections"]?["science"]?.AsArray()?.Count);
        }
    }
}
