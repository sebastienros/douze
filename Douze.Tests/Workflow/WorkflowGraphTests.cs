using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow;

public class WorkflowGraphTests
{
    private static Step Map(StepDefinition d) => new FakeMapStep(d);

    private static WorkflowValidationException Invalid(FakeWorkflow workflow) => Assert.Throws<WorkflowValidationException>(workflow.Build);

    [Fact]
    public void TopologicalOrderIsNameStable()
    {
        var a = new FakeWorkflow()
            .Add("pages", Map, inputs: ["**"])
            .Add("zeta", Map, needs: ["pages"])
            .Add("alpha", Map, needs: ["pages"])
            .Add("data", Map, inputs: ["**"])
            .Add("render", Map, needs: ["zeta", "alpha", "data"]);

        var b = new FakeWorkflow()
            .Add("render", Map, needs: ["data", "alpha", "zeta"])
            .Add("data", Map, inputs: ["**"])
            .Add("alpha", Map, needs: ["pages"])
            .Add("zeta", Map, needs: ["pages"])
            .Add("pages", Map, inputs: ["**"]);

        string[] expected = ["data", "pages", "alpha", "zeta", "render"];

        Assert.Equal(expected, a.Build().Nodes.Select(x => x.Definition.Name));
        Assert.Equal(expected, b.Build().Nodes.Select(x => x.Definition.Name));
        Assert.Equal([0, 1, 2, 3, 4], a.Build().Nodes.Select(x => x.Index));
    }

    [Fact]
    public void CycleIsReportedWithItsPath()
    {
        var e = Invalid(new FakeWorkflow()
            .Add("source-files", Map, inputs: ["**"])
            .Add("a", Map, needs: ["c", "source-files"])
            .Add("b", Map, needs: ["a"])
            .Add("c", Map, needs: ["b"]));

        Assert.Equal("steps form a cycle: a -> b -> c -> a", Assert.Single(e.Errors).Message);
    }

    [Fact]
    public void UnknownNeedsSuggestsTheClosestName()
    {
        var e = Invalid(new FakeWorkflow()
            .Add("markdown", Map, inputs: ["**"])
            .Add("render", Map, needs: ["markdwn"])
            .Add("other", Map, needs: ["unrelated"]));

        Assert.Equal(
            ["step 'render' needs unknown step 'markdwn'; did you mean 'markdown'?", "step 'other' needs unknown step 'unrelated'"],
            e.Errors.Select(x => x.Message));
    }

    [Fact]
    public void StepWithoutNeedsOrInputsIsInvalid()
    {
        var e = Invalid(new FakeWorkflow().Add("orphan", Map));

        Assert.Equal("step 'orphan' has no 'needs' and no 'inputs', so nothing enters it", Assert.Single(e.Errors).Message);
    }

    [Fact]
    public void GroupByOnStepThatDoesNotSupportIt()
    {
        var e = Invalid(new FakeWorkflow()
            .Add("markdown", Map, inputs: ["**"], groupBy: "dir")
            .Add("grouped", d => new FakeGroupStep(d), needs: ["markdown"], groupBy: "dir"));

        Assert.Equal("step 'markdown' does not support 'group-by'", Assert.Single(e.Errors).Message);
    }

    [Fact]
    public void SourceIsAReservedName()
    {
        var e = Invalid(new FakeWorkflow().Add("source", Map, inputs: ["**"]));

        Assert.Equal("'source' is a reserved step name", Assert.Single(e.Errors).Message);
    }

    [Fact]
    public void RootsAndTerminals()
    {
        var graph = new FakeWorkflow()
            .Add("pages", Map, inputs: ["**/*.md"])
            .Add("data", Map, inputs: ["**/*.yml"])
            .Add("extra", Map, inputs: ["**/*.txt"], needs: ["data"])
            .Add("render", Map, needs: ["pages", "extra"])
            .Add("search", Map, needs: ["pages"])
            .Build();

        Assert.Equal(["data", "pages"], graph.Roots.Select(x => x.Definition.Name));
        Assert.Equal(["render", "search"], graph.Terminals.Select(x => x.Definition.Name));
        Assert.Equal(["data", "extra", "pages"], graph.Ancestors("render").Order(StringComparer.Ordinal));
        Assert.Equal(["render"], graph["extra"].Dependents.Select(x => x.Definition.Name));
        Assert.Contains("no 'write' step: nothing will be written to the output folder", graph.Warnings);
    }
}
