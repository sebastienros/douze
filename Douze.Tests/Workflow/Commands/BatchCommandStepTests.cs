using Douze.FileSystems;
using Douze.Progress;
using Douze.Workflow;
using Douze.Workflow.Commands;
using Xunit;

namespace Douze.Tests.Workflow.Commands;

public class BatchCommandStepTests : CommandTestBase
{
    private string TempRoot => Path.Combine(TempFolder, "cache", "tmp");

    private async Task WritePagesAsync()
    {
        await Source.WriteTextFileAsync("/blog/b.html", "---\ntitle: B\n---\nb");
        await Source.WriteTextFileAsync("/blog/a.html", "---\ntitle: A\n---\na");
        await Source.WriteTextFileAsync("/docs/c.html", "---\ntitle: C\n---\nc");
        await Source.WriteTextFileAsync("/style.css", "body {}");
    }

    [Fact]
    public async Task InputsMaterializedWithManifest()
    {
        await WritePagesAsync();

        var result = await RunSuccessfullyAsync(Workflow(
            Step("pages", "frontmatter", inputs: ["**"]),
            Command("titles", Tool("batch-manifest", "{{ inputs }}", "{{ outputs }}"), CommandMode.Batch, needs: ["pages"], match: ["**/*.html"])));

        var run = result["titles"];
        Assert.Equal(3, run.FilesConsumed);
        Assert.Equal(1, run.FilesPassedThrough);

        // Sorted by path, with the content type and the metadata of each file.
        Assert.Equal(
            "/blog/a.html:text/html:A\n/blog/b.html:text/html:B\n/docs/c.html:text/html:C",
            await OutputTextAsync(run, "/titles.txt"));
    }

    [Fact]
    public async Task OutputsReadBackWithGlobs()
    {
        await WritePagesAsync();

        var result = await RunSuccessfullyAsync(Workflow(
            Command("copy", Tool("batch-copy", "{{ inputs }}", "{{ outputs }}"), CommandMode.Batch, inputs: ["**"], match: ["**/*.html"], outputs: ["**/*.html"])));

        var run = result["copy"];

        // pid.txt does not match 'outputs'; the consumed inputs are replaced by the outputs.
        var outputs = Outputs(run);
        Assert.Equal(["/blog/a.html", "/blog/b.html", "/docs/c.html"], outputs.Select(x => x.Path.FullName));
        Assert.Equal("---\nTITLE: A\n---\nA", await TextAsync(outputs[0]));

        // Derived from every input of the unit, in path order.
        Assert.Equal("copy", outputs[0].Provenance.Step);
        Assert.Equal(["/blog/a.html", "/blog/b.html", "/docs/c.html"], outputs[0].Provenance.Inputs.Select(x => x.Path.FullName));
        Assert.Equal("/blog/", outputs[0].Metadata.Directory.FullName);
        Assert.Empty(outputs[0].Metadata.Own);
    }

    [Fact]
    public async Task OutputsManifestMetadata()
    {
        await WritePagesAsync();

        var result = await RunSuccessfullyAsync(Workflow(
            Command("titles", Tool("batch-manifest", "{{ inputs }}", "{{ outputs }}"), CommandMode.Batch, inputs: ["**/*.html"])));

        var titles = Outputs(result["titles"]).Single();
        Assert.Equal("/titles.txt", titles.Path.FullName);
        Assert.Equal("text/x-titles", titles.ContentType);
        Assert.Equal(3m, titles.Metadata.Own["count"]!.GetValue<decimal>());
        Assert.Equal("titles", titles.Metadata.Own["step"]!.GetValue<string>());
    }

    [Fact]
    public async Task NoMaterializationWithoutInputsOrOutputs()
    {
        await WritePagesAsync();

        // With --verbose, the unit folders are kept: there is none.
        var result = await RunSuccessfullyAsync(Workflow(
            Command("index", Tool("args", "{{ step }}"), CommandMode.Batch, inputs: ["**"])), verbose: true);

        var run = result["index"];
        Assert.Equal(4, run.FilesConsumed);
        Assert.Empty(Outputs(run));
        Assert.Equal(0, run.FilesOut);
        Assert.False(Directory.Exists(TempRoot) && Directory.EnumerateDirectories(TempRoot, "index", SearchOption.AllDirectories).Any());

        // stdout is forwarded as debug messages.
        Assert.Contains(Messages, x => x is { Level: LogLevel.Debug, Step: "index", Message: "index" });
    }

    [Fact]
    public async Task GroupByRunsOneProcessPerGroup()
    {
        await WritePagesAsync();

        var result = await RunSuccessfullyAsync(Workflow(
            Command("copy", Tool("batch-copy", "{{ inputs }}", "{{ outputs }}"), CommandMode.Batch, inputs: ["**/*.html"], groupBy: "dir")));

        var run = result["copy"];
        Assert.Equal(["/blog/", "/docs/"], run.Results.Select(x => x.Key.Unit));

        var blog = run.Results[0].Outputs.Select(x => x.Path.FullName).ToArray();
        var docs = run.Results[1].Outputs.Select(x => x.Path.FullName).ToArray();
        Assert.Equal(["/blog/a.html", "/blog/b.html", "/pid.txt"], blog);
        Assert.Equal(["/docs/c.html", "/pid.txt"], docs);

        var pids = await Task.WhenAll(run.Results.Select(x => TextAsync(x.Outputs.Single(o => o.Path == "/pid.txt"))));
        Assert.NotEqual(pids[0], pids[1]);
    }

    [Fact]
    public async Task TempFolderRemoved()
    {
        await WritePagesAsync();

        var workflow = Workflow(Command("copy", Tool("batch-copy", "{{ inputs }}", "{{ outputs }}"), CommandMode.Batch, inputs: ["**/*.html"]));

        await RunSuccessfullyAsync(workflow);

        // Nothing left: not the unit folder, the step folder, nor the build's temp root.
        Assert.Empty(Directory.GetFileSystemEntries(TempRoot));

        // --verbose keeps the unit folder: inputs, manifest and outputs.
        await RunSuccessfullyAsync(workflow, verbose: true);

        var unit = Path.Combine(Assert.Single(Directory.GetDirectories(TempRoot)), "copy", "all");
        Assert.True(File.Exists(Path.Combine(unit, "in", "blog", "a.html")));
        Assert.True(File.Exists(Path.Combine(unit, "in", BatchCommandStep.InputsManifest)));
        Assert.True(File.Exists(Path.Combine(unit, "out", "pid.txt")));
    }

    [Fact]
    public async Task FailureMessages()
    {
        await WritePagesAsync();

        var result = await RunAsync(Workflow(
            Command("fail", Tool("fail", "1"), CommandMode.Batch, inputs: ["**/*.html"]),
            Command("group", Tool("fail", "2"), CommandMode.Batch, inputs: ["**/*.html"], groupBy: "dir")));

        Assert.Equal($"command exited with code 1: first line{Environment.NewLine}it failed", result["fail"].Errors.Single().Message);

        // The first group that fails stops the other, unless it failed at the same time.
        Assert.NotEmpty(result["group"].Errors);
        Assert.All(result["group"].Errors, x => Assert.Matches(@"^command exited with code 2 for group '/(blog|docs)/': first line\r?\nit failed$", x.Message));
    }
}
