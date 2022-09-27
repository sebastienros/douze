using System.Linq;
using System.Threading.Tasks;
using Douze.FileSystems;
using Xunit;

namespace Douze.Tests;

public class CommonTests
{
    MemoryFileSystem _sourceFs;
    MemoryFileSystem _outputFs;
    Options _options;

    public CommonTests()
    {
        _sourceFs = new MemoryFileSystem();
        _outputFs = new MemoryFileSystem();

        _options = new Options(_sourceFs, _outputFs, null, null, null);

        ConfigureProcessors();
    }

    private void ConfigureProcessors()
    {
        _options.BuildDefaultPipeline();
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
        await _sourceFs.CreateTemplateAsync("/index.html", "Hello World", new { title = "article", layout = "layout.html" });
        await _sourceFs.CreateTemplateAsync("/_includes/layout.html", "{{title}} BEFORE {{ content }} AFTER");

        await SiteHelper.BuildSiteAsync(_options);

        // 1 File and 1 Directory entries
        Assert.Equal(2, (await _outputFs.EnumerateFileSystemEntriesAsync(FPath.Root, true, null)).Count());

        Assert.True((await _outputFs.GetFileEntryAsync("/index.html")).Exists);
        Assert.Equal("article BEFORE Hello World AFTER", await _outputFs.ReadAllTextAsync("/index.html"));
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

        Assert.NotNull(_options.SiteData["collections"]);

        Assert.Equal(5, _options.SiteData["collections"]?["all"]?.AsArray()?.Count);
        Assert.Equal(4, _options.SiteData["collections"]?["article"]?.AsArray()?.Count);
        Assert.Equal(2, _options.SiteData["collections"]?["music"]?.AsArray()?.Count);
        Assert.Equal(2, _options.SiteData["collections"]?["science"]?.AsArray()?.Count);
    }

    [Fact]
    public async Task ShouldProcessFilesInCustomFolders()
    {
        _sourceFs = new MemoryFileSystem();
        _outputFs = new MemoryFileSystem();

        _options = new Options(_sourceFs, _outputFs, new[] { "myData" }, new[] { "myContent" }, new[] { "myIncludes" });

        ConfigureProcessors();

        await _sourceFs.CreateTemplateAsync("/myContent/index.html", "Hello World");
        await _sourceFs.WriteTextFileAsync("/myData/theme.yml", "layout: layout.html");
        await _sourceFs.CreateTemplateAsync("/myIncludes/layout.html", "BEFORE {{ content }} AFTER");

        await SiteHelper.BuildSiteAsync(_options);

        // 1 File and 1 Directory entries
        Assert.Equal(2, (await _outputFs.EnumerateFileSystemEntriesAsync(FPath.Root, true, null)).Count());

        Assert.True((await _outputFs.GetFileEntryAsync("/index.html")).Exists);
        Assert.Equal("BEFORE Hello World AFTER", await _outputFs.ReadAllTextAsync("/index.html"));

    }
}