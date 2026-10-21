using Douze.Cli;
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
    public async Task WorkflowFilesAreNotAvailableYet()
    {
        await File.WriteAllTextAsync(Path.Combine(_folder, WorkflowSource.DefaultFileName), "steps: {}", TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<WorkflowValidationException>(() => WorkflowSource.LoadAsync(CommandLine.Parse(["-s", _folder]), _folder));

        Assert.Equal("workflow files are not available yet", error.Errors.Single().Message);
    }
}
