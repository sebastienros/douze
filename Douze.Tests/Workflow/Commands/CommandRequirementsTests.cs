using Douze.FileSystems;
using Douze.Progress;
using Douze.Tracing;
using Douze.Workflow;
using Douze.Workflow.Commands;
using Xunit;

namespace Douze.Tests.Workflow.Commands;

public class CommandRequirementsTests : CommandTestBase
{
    private const string MissingTool = "douze-missing-tool-4ae1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("node script.mjs", true, "node")]
    [InlineData("FOO=1 BAR=\"a b\" node script.mjs", true, "node")]
    [InlineData("FOO=1 node", false, "FOO=1")]
    [InlineData("\"/a folder/tool\" --flag", true, "/a folder/tool")]
    [InlineData("npm;ls", true, "npm")]
    [InlineData("npm;ls", false, "npm;ls")]
    [InlineData("{{ site }}/tool", true, null)]
    [InlineData("$TOOL x", true, null)]
    [InlineData("(cd docs && make)", true, null)]
    [InlineData("'unclosed", true, null)]
    [InlineData("", true, null)]
    public void FirstWord(string run, bool shell, string? expected)
    {
        Assert.Equal(expected, CommandRequirements.FirstWord(run, shell));
    }

    [Fact]
    public async Task AMissingExecutableFailsTheBuildBeforeItRuns()
    {
        await Source.WriteTextFileAsync("/a.txt", "x");

        var result = await RunAsync(Workflow(Command("tool", $"{MissingTool} --flag", CommandMode.Filter, inputs: ["**"])));

        Assert.Equal(
            $"error: tool: {MissingTool} is not on PATH: install {MissingTool} and add its folder to PATH, or change the 'run' line of step 'tool'",
            Assert.Single(result.Errors).ToString());
        Assert.Equal(0, result["tool"].UnitsExecuted);
    }

    [Fact]
    public async Task ExecutablesAreFoundOnPathOrRelativeToTheWorkingDirectory()
    {
        var site = Path.Combine(TempFolder, "site");
        Directory.CreateDirectory(Path.Combine(site, "tools"));
        Site = await SiteOptions.DetectAsync(Source, Output, site, null);

        var script = OperatingSystem.IsWindows() ? "run.cmd" : "run.sh";
        await File.WriteAllTextAsync(Path.Combine(site, "tools", script), "echo hi", Ct);

        var results = await CheckAsync(
            Command("dotnet", Tool("args"), CommandMode.Filter, inputs: ["**"]),
            Command("relative", $"./{script} a", CommandMode.Filter, inputs: ["**"], cwd: "tools"),
            Command("absent", "./absent.sh", CommandMode.Filter, inputs: ["**"]),
            Command("builtin", "echo hello", CommandMode.Filter, inputs: ["**"]),
            Command("template", "{{ cwd }}/tool", CommandMode.Filter, inputs: ["**"]),
            Command("folder", "dotnet --version", CommandMode.Filter, inputs: ["**"], cwd: "missing"));

        var dotnet = results.Single(x => x.Step == "dotnet");
        Assert.Equal(RequirementState.Found, dotnet.Status.State);
        Assert.Equal("dotnet", Path.GetFileNameWithoutExtension(dotnet.Status.Path));

        var relative = results.Where(x => x.Step == "relative").ToArray();
        Assert.Equal(["working directory", $"./{script}"], relative.Select(x => x.Requirement.Name));
        Assert.Equal(RequirementStatus.Found(path: Path.Combine(site, "tools")), relative[0].Status);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(RequirementStatus.Found(path: Path.Combine(site, "tools", script)), relative[1].Status);
        }
        else
        {
            // Not executable yet.
            Assert.Equal(RequirementState.Missing, relative[1].Status.State);
            Assert.StartsWith("is not executable", relative[1].Status.Detail);

            File.SetUnixFileMode(Path.Combine(site, "tools", script), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var again = await CheckAsync(Command("relative", $"./{script} a", CommandMode.Filter, inputs: ["**"], cwd: "tools"));
            Assert.Equal(RequirementStatus.Found(path: Path.Combine(site, "tools", script)), again[1].Status);
        }

        var absent = Assert.Single(results, x => x.Step == "absent");
        Assert.Equal(RequirementStatus.Missing($"does not exist at '{Path.Combine(site, "absent.sh")}'"), absent.Status);
        Assert.Equal("check the 'run' line and the 'cwd' of step 'absent': a relative path is resolved against the working directory", absent.Requirement.Hint);

        var builtin = Assert.Single(results, x => x.Step == "builtin");
        Assert.Equal(("echo", RequirementState.Found, "is a command of the shell"), (builtin.Requirement.Name, builtin.Status.State, builtin.Status.Detail));

        var template = Assert.Single(results, x => x.Step == "template");
        Assert.Equal(RequirementState.Unchecked, template.Status.State);

        var folder = results.First(x => x.Step == "folder");
        Assert.Equal(RequirementStatus.Missing($"'{Path.Combine(site, "missing")}' does not exist"), folder.Status);
    }

    private async Task<IReadOnlyList<RequirementResult>> CheckAsync(params StepDefinition[] steps)
    {
        var graph = WorkflowGraph.Build(Workflow(steps));
        await using var context = await BuildContext.CreateAsync(Site!, new BuildOptions { UseCache = false }, SilentProgressView.Discard, NullTraceRecorder.Instance, Ct);
        return await Requirements.CheckAsync(graph, context, Ct);
    }
}
