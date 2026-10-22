using Douze.Progress;
using Douze.Tracing;
using Douze.Workflow;
using Douze.Workflow.Scheduling;
using Xunit;

namespace Douze.Tests.Workflow;

public class RequirementTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Requirement Met(string name) => Requirement.Create(name, "", _ => ValueTask.FromResult(RequirementStatus.Found("1.0", "/bin/" + name)));

    private static Requirement Missing(string name) => Requirement.Create(name, $"install {name}", _ => ValueTask.FromResult(RequirementStatus.Missing("is not on PATH")));

    private static Requirement Installable(string name) => Requirement.Create(name, "", _ => ValueTask.FromResult(RequirementStatus.Installable("is not installed")));

    private static Requirement Unchecked(string name) => Requirement.Create(name, "", _ => ValueTask.FromResult(RequirementStatus.Unchecked("is a template")));

    [Fact]
    public async Task AMissingRequirementFailsTheBuildBeforeAnyUnitRuns()
    {
        var site = await new FakeSite().WithFilesAsync("/a.txt", "/b.txt");
        FakeMapStep? first = null;
        FakeMapStep? second = null;

        var graph = new FakeWorkflow()
            .Add("first", d => first = new FakeMapStep(d) { Required = [Met("node")] }, inputs: ["**"])
            .Add("second", d => second = new FakeMapStep(d) { Required = [Installable("engine"), Missing("tool"), Missing("other")] }, needs: ["first"])
            .Build();

        var result = await site.BuildAsync(graph);

        Assert.False(result.Succeeded);
        Assert.Equal(["error: second: tool is not on PATH: install tool", "error: second: other is not on PATH: install other"], result.Errors.Select(x => x.ToString()));
        Assert.Empty(first!.Started);
        Assert.Empty(second!.Started);
        Assert.All(result.Steps, x => Assert.Equal(StepState.Skipped, x.State));
        Assert.Empty(await site.Output.EnumerateFileSystemEntriesAsync(Douze.FileSystems.FPath.Root, recursive: true, filter: null));
    }

    [Fact]
    public async Task RequirementsFoundInstallableOrUncheckedLetTheBuildRun()
    {
        var site = await new FakeSite().WithFilesAsync("/a.txt");

        var graph = new FakeWorkflow()
            .Add("first", d => new FakeMapStep(d) { Required = [Met("node"), Installable("engine"), Unchecked("command")] }, inputs: ["**"])
            .Build();

        var result = await site.BuildAsync(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(1, result["first"].UnitsExecuted);
    }

    [Fact]
    public async Task ChecksFollowTheTopologicalOrderAndAFailedCheckIsMissing()
    {
        var site = await new FakeSite().WithFilesAsync("/a.txt");

        var graph = new FakeWorkflow()
            .Add("z-last", d => new FakeMapStep(d) { Required = [Met("b")] }, needs: ["a-first"])
            .Add("a-first", d => new FakeMapStep(d)
            {
                Required = [Met("a"), Requirement.Create("broken", "fix it", _ => throw new InvalidOperationException("boom"))],
            }, inputs: ["**"])
            .Build();

        await using var context = await BuildContext.CreateAsync(site.Options, new BuildOptions(), SilentProgressView.Discard, NullTraceRecorder.Instance, Ct);
        var results = await Requirements.CheckAsync(graph, context, Ct);

        Assert.Equal([("a-first", "a"), ("a-first", "broken"), ("z-last", "b")], results.Select(x => (x.Step, x.Requirement.Name)));
        Assert.Equal(RequirementStatus.Missing("cannot be checked (boom)"), results[1].Status);
        Assert.Equal("broken cannot be checked (boom): fix it", results[1].Requirement.Message(results[1].Status));
    }
}
