using Douze.FileSystems;
using Douze.Nodes;
using Douze.Progress;
using Douze.Tests.Workflow.Cache;
using Douze.Workflow;
using Douze.Workflow.Steps;
using Xunit;

namespace Douze.Tests.Workflow.Steps;

public class CheckCodeStepTests : CacheTestBase
{
    private static WorkflowDefinition Check(NodeObject? with = null) => StepTestBase.Workflow(
        StepTestBase.Step("pages", "frontmatter", inputs: ["docs/**/*.md"]),
        StepTestBase.Step("markdown", "markdown", needs: ["pages"]),
        StepTestBase.Step("check", "check-code", needs: ["markdown"], with: with),
        StepTestBase.Step("write", "write", needs: ["check"]));

    private static string Fence(string code, string language = "yaml", string meta = "") => $"```{language} {meta}\n{code}\n```\n\n";

    private async Task<string[]> ErrorsAsync(WorkflowDefinition workflow)
    {
        var result = await BuildAsync(workflow, Options());
        Assert.False(result.Succeeded);
        return [.. result.Errors.SelectMany(x => x.Message.Split(Environment.NewLine))];
    }

    [Fact]
    public async Task ValidFencesPassThrough()
    {
        await Source.WriteTextFileAsync("/docs/a.md", "# A\n\n" + Fence("a: 1\nb: [1, 2]") + Fence("{ \"a\": 1 }", "json") + Fence("<a><b/></a>", "xml")
            + Fence("{% if x %}{{ x }}{% endif %}", "liquid") + Fence("not: [valid", "sh"));

        var result = await BuildCachedAsync(Check());

        Assert.Same(Outputs(result["markdown"]).Single(), result["check"].Results.Single().Files.Single());
    }

    [Fact]
    public async Task ErrorsNameThePageTheFenceAndTheLine()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("echo {", "sh") + Fence("a: 1") + Fence("a: 1\nb: [1, 2\nc: 3") + Fence("{ \"a\": }", "json"));

        var errors = await ErrorsAsync(Check());

        Assert.Equal(2, errors.Length);
        Assert.StartsWith("/docs/a.md: yaml fence 2 ('a: 1'): line 3, column ", errors[0], StringComparison.Ordinal);
        Assert.StartsWith("/docs/a.md: json fence 3 ('{ \"a\": }'): line 1, column 8: ", errors[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkflowFencesAreCheckedAgainstTheSchema()
    {
        await Source.WriteTextFileAsync("/docs/a.md",
            Fence("steps:\n  pages:\n    uses: frontmatter\n    with:\n      requires: [title]", meta: "title=\"douze.yml\" {2-3}")
            + Fence("render:\n  needs: pages\n  uses: liquidd", meta: "title=\"site/douze.yml\"")
            + Fence("steps:\n  pages:\n    uses: nothing", meta: "title=\"other.yml\""));

        var errors = await ErrorsAsync(Check());

        Assert.Equal(2, errors.Length);
        Assert.StartsWith("/docs/a.md: yaml fence 1 ('steps:'): line 2, column 3: ", errors[0], StringComparison.Ordinal);
        Assert.Contains("requires", errors[0], StringComparison.Ordinal);
        Assert.StartsWith("/docs/a.md: yaml fence 2 ('render:'): line 1, column 1: unknown built-in step 'liquidd'", errors[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkflowOptionNamesTheTitles()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("steps:\n  pages:\n    uses: nothing", meta: "title=\"douze.yml\""));

        await BuildCachedAsync(Check(StepTestBase.With(("workflow", new NodeArray()))));
    }

    [Fact]
    public async Task SkipLeavesAFenceOut()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("a: [", meta: "nocheck") + Fence("b: [", meta: "title=\"x.yml\" broken"));

        // nocheck by default; another word replaces it.
        Assert.StartsWith("/docs/a.md: yaml fence 1 ('b: ['): ", Assert.Single(await ErrorsAsync(Check())), StringComparison.Ordinal);
        Assert.StartsWith("/docs/a.md: yaml fence 1 ('a: ['): ", Assert.Single(await ErrorsAsync(Check(StepTestBase.With(("skip", NodeValue.Create("broken")))))), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LanguagesAndAliasesSelectTheValidators()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("a: [") + Fence("services: [", "compose"));

        await BuildCachedAsync(Check(StepTestBase.With(("languages", NodeValue.Create("json")))));

        var errors = await ErrorsAsync(Check(StepTestBase.With(("aliases", new NodeObject { ["compose"] = NodeValue.Create("yaml") }))));
        Assert.Equal(2, errors.Length);
        Assert.StartsWith("/docs/a.md: compose fence 2 ('services: ['): ", errors[1], StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidOptionsAreWorkflowErrors()
    {
        static void Create(params (string Key, Node? Value)[] options) => _ = new CheckCodeStep(StepTestBase.Step("check", "check-code", with: StepTestBase.With(options)));

        Assert.Contains("unknown language 'yml'", Assert.Throws<WorkflowValidationException>(() => Create(("languages", NodeValue.Create("yml")))).Message, StringComparison.Ordinal);
        Assert.Contains("'svg' of step 'check' is checked by 'xml'", Assert.Throws<WorkflowValidationException>(() => Create(("aliases", new NodeObject { ["svg"] = NodeValue.Create("json") }))).Message, StringComparison.Ordinal);
        Assert.Contains("unknown validator 'toml'", Assert.Throws<WorkflowValidationException>(() => Create(("aliases", new NodeObject { ["toml"] = NodeValue.Create("toml") }))).Message, StringComparison.Ordinal);
        Assert.Throws<WorkflowValidationException>(() => Create(("errors", NodeValue.Create("ignore"))));
        Assert.Throws<WorkflowValidationException>(() => Create(("languagues", NodeValue.Create("yaml"))));
    }

    [Fact]
    public async Task IgnoreLeavesPagesOut()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("a: ["));
        await Source.WriteTextFileAsync("/docs/legacy/b.md", Fence("b: ["));

        var errors = await ErrorsAsync(Check(StepTestBase.With(("ignore", NodeValue.Create("docs/legacy/**")))));

        Assert.StartsWith("/docs/a.md: ", Assert.Single(errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WarningsAreReportedAgainWhenThePageIsReplayed()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("a: ["));
        await Source.WriteTextFileAsync("/docs/b.md", Fence("b: 1"));
        var workflow = Check(StepTestBase.With(("errors", NodeValue.Create("warn"))));

        await BuildCachedAsync(workflow);
        var warning = Assert.Single(Messages, x => x.Level == LogLevel.Warning).Message;
        Assert.StartsWith("/docs/a.md: yaml fence 1 ('a: ['): line ", warning, StringComparison.Ordinal);

        Messages.Clear();
        var result = await BuildCachedAsync(workflow);

        Assert.Empty(Executed(result, "check"));
        Assert.Equal(warning, Assert.Single(Messages, x => x.Level == LogLevel.Warning).Message);
    }

    [Fact]
    public async Task LiquidFencesKnowTheShortcodes()
    {
        await Source.WriteTextFileAsync("/_includes/shortcodes/badge.liquid", "---\nargs: [text]\n---\n<span>{{ text }}</span>");
        await Source.WriteTextFileAsync("/docs/a.md", Fence("{% badge \"new\" %}", "liquid") + Fence("{% for x in y %}", "liquid"));

        var error = Assert.Single(await ErrorsAsync(Check()));

        Assert.StartsWith("/docs/a.md: liquid fence 2 ('{% for x in y %}'): ", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OtherFilesAreCheckedWhole()
    {
        await Source.WriteTextFileAsync("/feed.xml", "<rss>\n  <channel>\n</rss>");
        await Source.WriteTextFileAsync("/data.json", "{ \"a\": 1 }");
        await Source.WriteTextFileAsync("/notes.txt", "{ not json");

        var workflow = StepTestBase.Workflow(
            StepTestBase.Step("files", "frontmatter", inputs: ["*.*"]),
            StepTestBase.Step("check", "check-code", needs: ["files"], match: ["**/*"]),
            StepTestBase.Step("write", "write", needs: ["check"]));

        var error = Assert.Single(await ErrorsAsync(workflow));

        Assert.StartsWith("/feed.xml: xml: line 3, column ", error, StringComparison.Ordinal);
    }

    private static VirtualFile[] Outputs(Douze.Workflow.Scheduling.StepRun run) => StepTestBase.Outputs(run);
}
