using Douze.Progress;
using Douze.Tracing;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow;

public sealed class BuildContextTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "douze-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TempRootDeletedAtTheEndUnlessVerbose(bool verbose)
    {
        var site = await new FakeSite().WithFilesAsync("/a.md");
        string? tempRoot = null;

        // A step that leaves a file in the temp root, like a batch unit kept with --verbose or a process that
        // outlived its command.
        var graph = new FakeWorkflow()
            .Add("leave", d => new FakeMapStep(d)
            {
                Process = async (file, context, ct) =>
                {
                    tempRoot = context.Build.TempRoot;
                    Directory.CreateDirectory(Path.Combine(tempRoot, "leave"));
                    await File.WriteAllTextAsync(Path.Combine(tempRoot, "leave", "left.txt"), "", ct);
                    return FileList.Of(file);
                },
            }, inputs: ["**"])
            .Build();

        var result = await SiteBuilder.BuildAsync(graph, site.Options, new BuildOptions
        {
            Verbose = verbose,
            CacheDirectory = _cache,
            Trace = NullTraceRecorder.Instance,
            View = SilentProgressView.Discard,
            Cancellation = TestContext.Current.CancellationToken,
        });

        Assert.True(result.Succeeded);
        Assert.StartsWith(Path.Combine(_cache, "tmp"), tempRoot);
        Assert.Equal(verbose, File.Exists(Path.Combine(tempRoot!, "leave", "left.txt")));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_cache, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
