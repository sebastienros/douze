using Douze.FileSystems;
using Douze.Tracing;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow.Cache;

public sealed class ColdStartTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "douze-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SecondProcessBuildsFromTheDiskCache()
    {
        // A site without a content folder: every file of the source folder is content, including the output folder and
        // the cache folder, which must not be read as sources.
        var source = Path.Combine(_root, "site");
        var output = Path.Combine(source, "_site");
        Directory.CreateDirectory(Path.Combine(source, "_includes"));
        await File.WriteAllTextAsync(Path.Combine(source, "_includes", "layout.html"), "<html>{{ content }}</html>", Ct);
        await File.WriteAllTextAsync(Path.Combine(source, "index.md"), "---\nlayout: layout.html\ntitle: Home\n---\n# {{ title }}", Ct);
        await File.WriteAllTextAsync(Path.Combine(source, "style.css"), "body {}", Ct);

        // Each build is a new process: a new BuildContext opens the cache from disk.
        async Task<BuildResult> BuildAsync()
        {
            var site = await SiteOptions.DetectAsync(new PhysicalFileSystem(source), new PhysicalFileSystem(output), source, output);
            var result = await SiteBuilder.BuildAsync(DefaultWorkflow.Create(site), site, new BuildOptions
            {
                MaxDegreeOfParallelism = 4,
                Trace = NullTraceRecorder.Instance,
                Cancellation = Ct,
            });

            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));
            return result;
        }

        var first = await BuildAsync();
        Assert.True(File.Exists(Path.Combine(source, ".douze", "cache", "index.bin")));

        var second = await BuildAsync();

        Assert.Equal(0, second.UnitsExecuted);
        Assert.Equal(first.UnitsExecuted, second.UnitsReplayed);
        Assert.Equal(["/index.html", "/style.css"], second.Outputs.Select(x => x.Path.FullName));
        Assert.Equal("<html><h1>Home</h1>\n</html>", await File.ReadAllTextAsync(Path.Combine(output, "index.html"), Ct));

        // An edit runs the units it affects only.
        await File.WriteAllTextAsync(Path.Combine(source, "style.css"), "body { color: red }", Ct);
        var third = await BuildAsync();

        Assert.Equal(["/style.css"], third.Steps.SelectMany(x => x.Results).Where(x => !x.Replayed).Select(x => x.Key.Unit).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal("body { color: red }", await File.ReadAllTextAsync(Path.Combine(output, "style.css"), Ct));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
