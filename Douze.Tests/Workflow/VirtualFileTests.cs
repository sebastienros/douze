using Douze.FileSystems;
using Douze.Nodes;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow;

public class VirtualFileTests
{
    [Fact]
    public async Task WithDerivesFromTheFileAndKeepsItsSourceDate()
    {
        var fs = new MemoryFileSystem();
        await fs.WriteTextFileAsync("/blog/post.md", "# Post");

        var source = VirtualFile.FromSource(fs, "/blog/post.md", await fs.GetFileMetadataAsync("/blog/post.md"));

        Assert.True(source.Provenance.IsSource);
        Assert.NotNull(source.LastWriteTimeUtc);
        Assert.Equal("/blog/", source.Metadata.Directory.FullName);

        var html = source.With("markdown", path: "/blog/post.html", content: FileContent.FromText("<h1>Post</h1>"), contentType: "text/html");

        // A step that runs before the one reading page.date must not lose the source's modification time.
        Assert.Equal(source.LastWriteTimeUtc, html.LastWriteTimeUtc);
        Assert.Equal("markdown", html.Provenance.Step);
        Assert.Equal([source.Ref], html.Provenance.Inputs);
        Assert.Same(source.Metadata, html.Metadata);
        Assert.Equal("text/html", html.ContentType);

        // What is not given is kept.
        var renamed = html.With("permalink", path: "/blog/post/index.html");
        Assert.Same(html.Content, renamed.Content);
        Assert.Equal("text/html", renamed.ContentType);
        Assert.Equal([html.Ref], renamed.Provenance.Inputs);

        // A file derived from several inputs has no single source date.
        var index = VirtualFile.Derived("index", [source, html], "/index.html", FileContent.Empty, Metadata.Empty, null);
        Assert.Null(index.LastWriteTimeUtc);
        Assert.Equal([source.Ref, html.Ref], index.Provenance.Inputs);
    }
}
