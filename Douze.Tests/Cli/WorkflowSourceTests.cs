using Douze.Cli;
using Douze.Progress;
using Douze.Tests.Workflow.Yaml;
using Douze.Tracing;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Cli;

public class WorkflowSourceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public WorkflowSourceTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "_content"));
    }

    public void Dispose()
    {
        Directory.Delete(_folder, true);
    }

    [Fact]
    public async Task DetectsTheSiteAndUsesTheDefaultWorkflow()
    {
        var (workflow, site) = await WorkflowSource.LoadAsync(CommandLine.Parse(["-s", _folder]), _folder);

        Assert.Equal(DefaultWorkflow.Create(site).Steps.Select(x => x.Name), workflow.Steps.Select(x => x.Name));
        Assert.Equal("/_content/", site.ContentFolder.FullName);
        Assert.Equal("/", site.DataFolder.FullName);
        Assert.Equal(_folder, site.SourceDirectory);
        Assert.Equal(Path.GetFullPath(WorkflowSource.DefaultOutput), site.OutputDirectory);
        Assert.Null(site.Parallelism);
    }

    [Fact]
    public async Task OutputOptionWins()
    {
        var output = Path.Combine(_folder, "out");

        var (_, site) = await WorkflowSource.LoadAsync(CommandLine.Parse(["-s", _folder, "-o", output]), _folder);

        Assert.Equal(output, site.OutputDirectory);
    }

    [Fact]
    public async Task SiteSectionSetsTheSite()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "_data"));
        await WriteAsync(WorkflowSource.DefaultFileName, """
            site:
              content: docs
              includes: templates
              output: public
              url: https://example.com
              parallelism: 3
            """);

        var (workflow, site) = await WorkflowSource.LoadAsync(CommandLine.Parse(["-s", _folder]), _folder);

        Assert.Equal("/docs/", site.ContentFolder.FullName);
        Assert.Equal("/_data/", site.DataFolder.FullName);      // not set: detected
        Assert.Equal("/templates/", site.IncludesFolder.FullName);
        Assert.Equal(Path.Combine(_folder, "public"), site.OutputDirectory);
        Assert.Equal("https://example.com", site.Url);
        Assert.Equal(3, site.Parallelism);

        // No 'steps': the default workflow over the folders of the 'site' section.
        Assert.Null(workflow.SourceFile);
        Assert.Equal(["docs/**", "!docs/**/*.{yml,yaml,json,xml}"], workflow.Steps.Single(x => x.Name == "pages").Inputs);
    }

    [Fact]
    public async Task OutputOptionWinsOverTheSiteSection()
    {
        var output = Path.Combine(_folder, "out");
        await WriteAsync(WorkflowSource.DefaultFileName, "site:\n  output: public\n");

        var (_, site) = await WorkflowSource.LoadAsync(CommandLine.Parse(["-s", _folder, "-o", output]), _folder);

        Assert.Equal(output, site.OutputDirectory);
    }

    [Fact]
    public async Task StepsOfTheWorkflowFile()
    {
        await WriteAsync(WorkflowSource.DefaultFileName, """
            steps:
              copy: { inputs: "**", uses: write }
            """);

        var (workflow, _) = await WorkflowSource.LoadAsync(CommandLine.Parse(["-s", _folder]), _folder);

        Assert.Equal("copy", Assert.Single(workflow.Steps).Name);
        Assert.Equal(Path.Combine(_folder, WorkflowSource.DefaultFileName), workflow.SourceFile);
    }

    [Fact]
    public async Task WorkflowOptionReplacesTheDefaultFile()
    {
        await WriteAsync(WorkflowSource.DefaultFileName, "steps:\n  default: { inputs: \"**\", uses: write }\n");
        await WriteAsync("other.yml", "steps:\n  other: { inputs: \"**\", uses: write }\n");

        var (workflow, _) = await WorkflowSource.LoadAsync(CommandLine.Parse(["-s", _folder, "-w", Path.Combine(_folder, "other.yml")]), _folder);

        Assert.Equal("other", Assert.Single(workflow.Steps).Name);
    }

    [Fact]
    public async Task MissingWorkflowFileIsAnError()
    {
        var path = Path.Combine(_folder, "missing.yml");

        var error = await Assert.ThrowsAsync<WorkflowValidationException>(() => WorkflowSource.LoadAsync(CommandLine.Parse(["-s", _folder, "-w", path]), _folder));

        Assert.Equal($"error: the workflow file '{path}' does not exist", Assert.Single(error.Errors).ToString());
    }

    [Fact]
    public async Task ErrorsPointAtTheWorkflowFile()
    {
        await WriteAsync(WorkflowSource.DefaultFileName, "steps:\n  write: { uses: write, if: true }\n");

        var error = await Assert.ThrowsAsync<WorkflowValidationException>(() => WorkflowSource.LoadAsync(CommandLine.Parse(["-s", _folder]), _folder));

        var location = Assert.Single(error.Errors).Location!.Value;
        Assert.Equal(Path.GetFullPath(Path.Combine(_folder, WorkflowSource.DefaultFileName)), Path.GetFullPath(location.File));
        Assert.Equal((2, 25), (location.Line, location.Column));
    }

    [Fact]
    public async Task TwoPipelineFixtureBuilds()
    {
        var source = Path.Combine(WorkflowLoaderTests.Fixtures, "two-pipelines");
        var output = Path.Combine(_folder, "out");

        var (workflow, site) = await WorkflowSource.LoadAsync(CommandLine.Parse(["-s", source, "-o", output]), source);

        Assert.Equal(2, site.Parallelism);
        Assert.Equal("https://example.com", site.Url);

        var result = await SiteBuilder.BuildAsync(WorkflowGraph.Build(workflow), site, new BuildOptions
        {
            MaxDegreeOfParallelism = site.Parallelism!.Value,
            Trace = NullTraceRecorder.Instance,
            View = SilentProgressView.Discard,
            Cancellation = TestContext.Current.CancellationToken,
        });

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(
            ["/blog/post/index.html", "/index.html", "/packages/postgres/index.html", "/packages/redis/index.html"],
            result.Outputs.Select(x => x.Path.FullName));

        Assert.Equal("<title>Home - Two pipelines</title><h1>Hello</h1>\n\n", await File.ReadAllTextAsync(Path.Combine(output, "index.html"), TestContext.Current.CancellationToken));
        Assert.Equal("<h1>redis 1.0</h1><p>Two pipelines</p>\n", await File.ReadAllTextAsync(Path.Combine(output, "packages", "redis", "index.html"), TestContext.Current.CancellationToken));
    }

    private Task WriteAsync(string name, string content)
    {
        return File.WriteAllTextAsync(Path.Combine(_folder, name), content, TestContext.Current.CancellationToken);
    }
}
