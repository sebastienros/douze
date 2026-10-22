using System.Collections.Concurrent;
using Douze.FileSystems;
using Douze.Progress;
using Douze.Tracing;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow.Steps;

public class WriteStepTests : StepTestBase
{
    [Fact]
    public async Task DuplicateOutputErrorText()
    {
        await Source.WriteTextFileAsync("/_content/a.md", "markdown");
        await Source.WriteTextFileAsync("/_content/a.html", "html");

        var result = await BuildAsync();

        var error = Assert.Single(result.Errors);
        Assert.Equal("write", error.Step);
        Assert.Equal("output '/a/index.html' is produced by both '/_content/a.html' and '/_content/a.md'", error.Message);
    }

    [Fact]
    public async Task DirectoriesCreated()
    {
        await Source.WriteTextFileAsync("/blog/2020/post.md", "post");
        await Source.WriteTextFileAsync("/images/logo.png", "png");

        var result = await BuildSuccessfullyAsync();

        Assert.True((await Output.GetDirectoryEntryAsync("/blog/2020/post/")).Exists);
        Assert.Equal("<p>post</p>\n", await Output.ReadAllTextAsync("/blog/2020/post/index.html"));

        // Files no step claims are written as they are.
        Assert.Equal("png", await Output.ReadAllTextAsync("/images/logo.png"));
        Assert.Equal(["/blog/2020/post/index.html", "/images/logo.png"], result.Outputs.Select(x => x.Path.FullName));
    }

    [Fact]
    public async Task IdenticalDuplicateIsWrittenOnce()
    {
        // Two pages using one image both add its variant: the same path, content and metadata.
        await Source.WriteTextFileAsync("/a.txt", "a");
        await Source.WriteTextFileAsync("/b.txt", "b");
        var output = new CountingFileSystem(Output);

        var graph = new FakeWorkflow()
            .Add("pages", d => new FakeMapStep(d)
            {
                Process = (f, context, _) =>
                {
                    context.AddOutput(new VirtualFile("/assets/shared.txt", FileContent.FromText("shared"), Metadata.Empty, null, Provenance.Source));
                    return new(FileList.Of(f));
                },
            }, inputs: ["**"])
            .Add("write", d => new Douze.Workflow.Steps.WriteStep(d), needs: ["pages"], uses: "write")
            .Build();

        var result = await SiteBuilder.BuildAsync(graph, new SiteOptions { Source = Source, Output = output }, Options());

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal("shared", await Output.ReadAllTextAsync("/assets/shared.txt"));
        Assert.Equal(1, output.Created["/assets/shared.txt"]);
        Assert.Equal(2, result["write"].Results.Count(x => x.Key.Unit == "/assets/shared.txt"));
    }

    [Fact]
    public async Task DuplicateWithAnotherContentFails()
    {
        await Source.WriteTextFileAsync("/a.txt", "a");
        await Source.WriteTextFileAsync("/b.txt", "b");

        var graph = new FakeWorkflow()
            .Add("pages", d => new FakeMapStep(d)
            {
                Process = (f, context, _) =>
                {
                    context.AddOutput(new VirtualFile("/assets/shared.txt", FileContent.FromText(f.Path.FullName), Metadata.Empty, null, Provenance.Source));
                    return new(FileList.Of(f));
                },
            }, inputs: ["**"])
            .Add("write", d => new Douze.Workflow.Steps.WriteStep(d), needs: ["pages"], uses: "write")
            .Build();

        var result = await SiteBuilder.BuildAsync(graph, new SiteOptions { Source = Source, Output = Output }, Options());

        var error = Assert.Single(result.Errors);
        Assert.Equal("output '/assets/shared.txt' is produced by both step 'pages' and step 'pages'", error.Message);
    }

    private static BuildOptions Options() => new()
    {
        MaxDegreeOfParallelism = 4,
        KeepFiles = true,
        Trace = NullTraceRecorder.Instance,
        View = SilentProgressView.Discard,
        Cancellation = TestContext.Current.CancellationToken,
    };

    /// <summary>
    /// Counts the files created in a file system.
    /// </summary>
    private sealed class CountingFileSystem(IFileSystem inner) : IFileSystem
    {
        public ConcurrentDictionary<string, int> Created { get; } = new();

        public ValueTask<Stream> CreateFileAsync(FPath path)
        {
            Created.AddOrUpdate(path.FullName, 1, static (_, n) => n + 1);
            return inner.CreateFileAsync(path);
        }

        public ValueTask<Stream> OpenFileAsync(FPath path) => inner.OpenFileAsync(path);
        public ValueTask<FileEntry> GetFileEntryAsync(FPath path) => inner.GetFileEntryAsync(path);
        public ValueTask<DirectoryEntry> GetDirectoryEntryAsync(FPath path) => inner.GetDirectoryEntryAsync(path);
        public ValueTask<IEnumerable<FileSystemEntry>> EnumerateFileSystemEntriesAsync(FPath basePath, bool recursive, Predicate<string>? filter) => inner.EnumerateFileSystemEntriesAsync(basePath, recursive, filter);
        public ValueTask CreateDirectoryAsync(FPath path) => inner.CreateDirectoryAsync(path);
        public ValueTask<FileMetadata> GetFileMetadataAsync(FPath path) => inner.GetFileMetadataAsync(path);
    }
}
