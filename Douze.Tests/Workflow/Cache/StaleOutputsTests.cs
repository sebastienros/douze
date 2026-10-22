using Douze.FileSystems;
using Xunit;

namespace Douze.Tests.Workflow.Cache;

/// <summary>
/// A build from the disk cache deletes the output files the previous builds wrote and it does not.
/// </summary>
public class StaleOutputsTests : CacheTestBase
{
    private async Task WriteSiteAsync()
    {
        await Source.WriteTextFileAsync("/_content/index.md", "# Home");
        await Source.WriteTextFileAsync("/_content/posts/a.md", "a");
        await Source.WriteTextFileAsync("/_content/posts/b.md", "b");
        await Source.WriteTextFileAsync("/_content/docs/deep/c.md", "c");
    }

    private async Task<bool> ExistsAsync(FPath path) => (await Output.GetFileEntryAsync(path)).Exists;

    [Fact]
    public async Task DeletedSourcesLoseTheirOutputs()
    {
        await WriteSiteAsync();
        await BuildCachedAsync();

        await Source.DeleteFileAsync("/_content/posts/a.md");
        await Source.DeleteFileAsync("/_content/docs/deep/c.md");
        var result = await BuildCachedAsync();

        Assert.Equal(["/docs/deep/c/index.html", "/posts/a/index.html"], result.Deleted.Select(x => x.FullName));
        Assert.False(await ExistsAsync("/posts/a/index.html"));
        Assert.True(await ExistsAsync("/posts/b/index.html"));

        // Folders left empty are deleted, up to the output folder.
        Assert.False((await Output.GetDirectoryEntryAsync("/posts/a/")).Exists);
        Assert.False((await Output.GetDirectoryEntryAsync("/docs/")).Exists);
        Assert.True((await Output.GetDirectoryEntryAsync("/posts/")).Exists);

        // Nothing else to delete.
        Assert.Empty((await BuildCachedAsync()).Deleted);
    }

    [Fact]
    public async Task FilesChangedSinceTheyWereWrittenStay()
    {
        await WriteSiteAsync();
        await BuildCachedAsync();

        await Output.WriteTextFileAsync("/posts/a/index.html", "kept: not what the build wrote");
        await Source.DeleteFileAsync("/_content/posts/a.md");
        var result = await BuildCachedAsync();

        Assert.Empty(result.Deleted);
        Assert.True(await ExistsAsync("/posts/a/index.html"));
    }

    [Fact]
    public async Task AFailedBuildDeletesNothing()
    {
        await WriteSiteAsync();
        await BuildCachedAsync();

        await Source.DeleteFileAsync("/_content/posts/a.md");
        await Source.WriteTextFileAsync("/_content/posts/b.md", "{% if %}");
        var failed = await BuildAsync(options: Options());

        Assert.False(failed.Succeeded);
        Assert.True(await ExistsAsync("/posts/a/index.html"));

        // The next build that succeeds deletes it: the cache kept what the builds before the failure wrote.
        await Source.WriteTextFileAsync("/_content/posts/b.md", "b again");
        var result = await BuildCachedAsync();

        Assert.Equal(["/posts/a/index.html"], result.Deleted.Select(x => x.FullName));
    }

    [Fact]
    public async Task WithoutTheCacheNothingIsDeleted()
    {
        await WriteSiteAsync();
        await BuildCachedAsync(cache: false);

        await Source.DeleteFileAsync("/_content/posts/a.md");
        var result = await BuildCachedAsync(cache: false);

        Assert.Empty(result.Deleted);
        Assert.True(await ExistsAsync("/posts/a/index.html"));
    }
}
