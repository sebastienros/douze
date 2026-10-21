using Douze.FileSystems;
using Xunit;

namespace Douze.Tests;

public class CommonTests : SiteTestBase
{
    [Fact]
    public async Task ShouldRenderHtmlFile()
    {
        await Source.WriteTextFileAsync("/index.html", "Hello World");

        await BuildSuccessfullyAsync();

        Assert.True((await Output.GetFileEntryAsync("/index.html")).Exists);
        Assert.Equal("Hello World", await Output.ReadAllTextAsync("/index.html"));
    }

    [Fact]
    public async Task ShouldRenderMarkdownFile()
    {
        await Source.WriteTextFileAsync("/article.md", "**Hello World**");

        await BuildSuccessfullyAsync();

        Assert.True((await Output.GetFileEntryAsync("/article/index.html")).Exists);
        Assert.Equal("<p><strong>Hello World</strong></p>\n", await Output.ReadAllTextAsync("/article/index.html"));
    }

    [Fact]
    public async Task ShouldGenerateFolderWithIndex()
    {
        await Source.WriteTextFileAsync("/index.md", "**Hello World**");

        await BuildSuccessfullyAsync();

        Assert.True((await Output.GetFileEntryAsync("/index.html")).Exists);
        Assert.Equal("<p><strong>Hello World</strong></p>\n", await Output.ReadAllTextAsync("/index.html"));
    }

    [Fact]
    public async Task ShouldRenderLayout()
    {
        await Source.CreateTemplateAsync("/index.html", "Hello World", new { title = "article", layout = "layout.html" });
        await Source.CreateTemplateAsync("/_includes/layout.html", "{{title}} BEFORE {{ content }} AFTER");

        await BuildSuccessfullyAsync();

        // 1 File and 1 Directory entries
        Assert.Equal(2, (await Output.EnumerateFileSystemEntriesAsync(FPath.Root, true, null)).Count());

        Assert.True((await Output.GetFileEntryAsync("/index.html")).Exists);
        Assert.Equal("article BEFORE Hello World AFTER", await Output.ReadAllTextAsync("/index.html"));
    }

    [Fact]
    public async Task ShouldLoadDataFiles()
    {
        await Source.CreateTemplateAsync("/index.html", "Hello World");
        await Source.WriteTextFileAsync("/_data/layout.yml", "layout.html");
        await Source.CreateTemplateAsync("/_includes/layout.html", "BEFORE {{ content }} AFTER");

        await BuildSuccessfullyAsync();

        // 1 File and 1 Directory entries
        Assert.Equal(2, (await Output.EnumerateFileSystemEntriesAsync(FPath.Root, true, null)).Count());

        Assert.True((await Output.GetFileEntryAsync("/index.html")).Exists);
        Assert.Equal("BEFORE Hello World AFTER", await Output.ReadAllTextAsync("/index.html"));
    }

    [Fact]
    public async Task ShouldRenderBaseLayout()
    {
        await Source.CreateTemplateAsync("/index.html", "Hello World", new { layout = "layout.html" });
        await Source.CreateTemplateAsync("/_includes/layout.html", "BEFORE {{ content }} AFTER", new { layout = "master.html" });
        await Source.CreateTemplateAsync("/_includes/master.html", "BEGIN {{ content }} END");

        await BuildSuccessfullyAsync();

        Assert.True((await Output.GetFileEntryAsync("/index.html")).Exists);
        Assert.Equal("BEGIN BEFORE Hello World AFTER END", await Output.ReadAllTextAsync("/index.html"));
    }

    [Fact]
    public async Task PermalinkShouldChangeFilename()
    {
        await Source.CreateTemplateAsync("/index.html", "Hello World", new { permalink = "posts/my-post" });

        await BuildSuccessfullyAsync();

        Assert.True((await Output.GetFileEntryAsync("/posts/my-post/index.html")).Exists);
        Assert.Equal("Hello World", await Output.ReadAllTextAsync("/posts/my-post/index.html"));
    }

    [Fact]
    public async Task CustomFileNameShouldUseFolders()
    {
        await Source.CreateTemplateAsync("/articles/article.md", "Hello World");

        await BuildSuccessfullyAsync();

        Assert.True((await Output.GetFileEntryAsync("/articles/article/index.html")).Exists);
    }

    [Fact]
    public async Task PermalinkShouldBeTemplated()
    {
        await Source.CreateTemplateAsync("/index.html", "Hello World", new { title = "My Blog Post", permalink = "posts//{{ title | slug }}/index.html" });

        await BuildSuccessfullyAsync();

        Assert.True((await Output.GetFileEntryAsync("/posts/my-blog-post/index.html")).Exists);
        Assert.Equal("Hello World", await Output.ReadAllTextAsync("/posts/my-blog-post/index.html"));
    }

    [Fact]
    public async Task PermalinkShouldPreserveCustomFilenames()
    {
        await Source.CreateTemplateAsync("/api.html", "{ \"error\": false }", new { permalink = "posts/api.json" });

        await BuildSuccessfullyAsync();

        Assert.True((await Output.GetFileEntryAsync("/posts/api.json")).Exists);
        Assert.Equal("{ \"error\": false }", await Output.ReadAllTextAsync("/posts/api.json"));
    }

    [Fact]
    public async Task TagsShouldBeAvailableInCollections()
    {
        await Source.CreateTemplateAsync("/articles/article1.md", "Hello World", new { tags = new[] { "article", "science" } });
        await Source.CreateTemplateAsync("/articles/article2.md", "Hello World", new { tags = new[] { "article", "music" } });
        await Source.CreateTemplateAsync("/articles/article3.md", "Hello World", new { tags = new[] { "article", "music", "science" } });
        await Source.CreateTemplateAsync("/articles/article4.md", "Hello World", new { tags = "article" });
        await Source.CreateTemplateAsync("/blog/post1.md", "Hello World");

        var result = await BuildSuccessfullyAsync();

        Assert.Equal(5, result.Collection("all").Count);
        Assert.Equal(4, result.Collection("article").Count);
        Assert.Equal(2, result.Collection("music").Count);
        Assert.Equal(2, result.Collection("science").Count);
    }

    [Fact]
    public async Task CollectionsShouldBeSortedByDate()
    {
        // Created in reverse order of their dates
        for (var i = 30; i > 0; i--)
        {
            await Source.CreateTemplateAsync($"/articles/a{i:00}.md", "Hello World", new { tags = "article", title = $"Article {i}", date = $"2020-01-{31 - i:00}" });
        }

        var result = await BuildSuccessfullyAsync();

        var titles = result.Collection("article").Select(x => x.Own["title"]?.GetValue<string>()).ToArray();

        Assert.Equal(Enumerable.Range(1, 30).Reverse().Select(i => $"Article {i}"), titles);
    }

    [Fact]
    public async Task CollectionsShouldBeSortedByPathWhenDatesAreEqual()
    {
        // Created in reverse order of their paths
        for (var i = 30; i > 0; i--)
        {
            await Source.CreateTemplateAsync($"/articles/a{i:00}.md", "Hello World", new { tags = "article", title = $"Article {i}", date = "2020-01-01" });
        }

        var result = await BuildSuccessfullyAsync();

        var titles = result.Collection("article").Select(x => x.Own["title"]?.GetValue<string>()).ToArray();

        Assert.Equal(Enumerable.Range(1, 30).Select(i => $"Article {i}"), titles);
    }

    [Fact]
    public async Task PaginationShouldOnlyLinkPagesOfTheSameDocument()
    {
        var template = "{{ pagination.pageNumber }}/{{ pagination.pages.size }}:{{ pagination.items | join: ',' }}";

        await Source.CreateTemplateAsync("/letters.html", template, new { pagination = new { data = "items", size = 1 }, items = new[] { "a", "b", "c" } });
        await Source.CreateTemplateAsync("/digits.html", template, new { pagination = new { data = "items", size = 1 }, items = new[] { "1", "2" } });

        await BuildSuccessfullyAsync();

        Assert.Equal("0/3:a", await Output.ReadAllTextAsync("/letters/index.html"));
        Assert.Equal("2/3:c", await Output.ReadAllTextAsync("/letters/2/index.html"));
        Assert.Equal("0/2:1", await Output.ReadAllTextAsync("/digits/index.html"));
        Assert.Equal("1/2:2", await Output.ReadAllTextAsync("/digits/1/index.html"));
    }

    [Fact]
    public async Task ShouldProcessFilesInCustomFolders()
    {
        Site = new SiteOptions
        {
            Source = Source,
            Output = Output,
            ContentFolder = "/myContent/",
            DataFolder = "/myData/",
            IncludesFolder = "/myIncludes/",
        };

        await Source.CreateTemplateAsync("/myContent/index.html", "Hello World");
        await Source.WriteTextFileAsync("/myData/layout.yml", "layout.html");
        await Source.CreateTemplateAsync("/myIncludes/layout.html", "BEFORE {{ content }} AFTER");

        await BuildSuccessfullyAsync();

        // 1 File and 1 Directory entries
        Assert.Equal(2, (await Output.EnumerateFileSystemEntriesAsync(FPath.Root, true, null)).Count());

        Assert.True((await Output.GetFileEntryAsync("/index.html")).Exists);
        Assert.Equal("BEFORE Hello World AFTER", await Output.ReadAllTextAsync("/index.html"));
    }
}
