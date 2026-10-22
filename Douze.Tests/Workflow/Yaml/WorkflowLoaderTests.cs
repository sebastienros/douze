using Douze.FileSystems;
using Douze.Nodes;
using Douze.Workflow;
using Douze.Workflow.Yaml;
using Xunit;

namespace Douze.Tests.Workflow.Yaml;

public class WorkflowLoaderTests
{
    internal static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "workflows");

    private static WorkflowFile Load(string yaml) => WorkflowLoader.Load(yaml, "douze.yml");

    private static WorkflowValidationError[] Errors(string yaml)
    {
        return [.. Assert.Throws<WorkflowValidationException>(() => Load(yaml)).Errors];
    }

    /// <summary>
    /// Loads <paramref name="yaml"/>, expects exactly one error and checks its message and position.
    /// </summary>
    private static void AssertError(string yaml, string message, int line, int column)
    {
        var error = Assert.Single(Errors(yaml));

        Assert.Equal(message, error.Message);
        Assert.Equal(new SourceLocation("douze.yml", line, column), error.Location);
    }

    /// <summary>
    /// Loads <paramref name="yaml"/> and builds its graph with the built-in steps, expecting one error.
    /// </summary>
    private static void AssertGraphError(string yaml, string message, int line, int column)
    {
        var workflow = Load(yaml).Workflow!;
        var error = Assert.Single(Assert.Throws<WorkflowValidationException>(() => WorkflowGraph.Build(workflow)).Errors);

        Assert.Equal(message, error.Message);
        Assert.Equal(new SourceLocation("douze.yml", line, column), error.Location);
    }

    // Loader rules (section 4.5).

    [Fact]
    public void UnknownTopLevelKey()
    {
        AssertError("""
            steps:
              write: { inputs: "**", uses: write }
            jobs: {}
            """, "unknown key 'jobs'; expected 'site' or 'steps'", 3, 1);
    }

    [Fact]
    public void RootMustBeAMapping()
    {
        AssertError("- a\n- b\n", "a workflow file must be a mapping with 'site' and 'steps'", 1, 1);
    }

    [Fact]
    public void StepsMustBeAMapping()
    {
        AssertError("steps:\n  - write\n", "'steps' must be a mapping of step names to steps", 2, 3);
    }

    [Fact]
    public void InvalidStepName()
    {
        AssertError("""
            steps:
              My Step: { inputs: "**", uses: write }
            """, "invalid step name 'My Step': use lowercase letters, digits and dashes", 2, 3);
    }

    [Fact]
    public void StepNameTooLong()
    {
        var name = new string('a', 65);

        AssertError($"steps:\n  {name}: {{ inputs: \"**\", uses: write }}\n", $"step name '{name}' is longer than 64 characters", 2, 3);
    }

    [Fact]
    public void ReservedStepName()
    {
        AssertError("""
            steps:
              source: { inputs: "**", uses: write }
            """, "'source' is a reserved step name", 2, 3);
    }

    [Fact]
    public void StepMustBeAMapping()
    {
        AssertError("steps:\n  write: [a]\n", "step 'write' must be a mapping of fields", 2, 10);
    }

    [Fact]
    public void UnknownStepField()
    {
        AssertError("""
            steps:
              render:
                inputs: "**"
                uses: liquid
                if: always
            """, "unknown field 'if' on step 'render'", 5, 5);
    }

    [Fact]
    public void UnknownStepFieldSuggestsTheClosestField()
    {
        AssertError("""
            steps:
              render: { inputs: "**", uses: liquid, need: pages }
            """, "unknown field 'need' on step 'render'; did you mean 'needs'?", 2, 41);
    }

    [Fact]
    public void NeedsMustBeStrings()
    {
        AssertError("""
            steps:
              render: { needs: { a: b }, uses: liquid }
            """, "'needs' must be a string or a list of strings", 2, 20);
    }

    [Fact]
    public void NeedsItemMustBeAString()
    {
        AssertError("""
            steps:
              render: { needs: [pages, [data]], uses: liquid }
            """, "'needs' must be a string or a list of strings", 2, 28);
    }

    [Theory]
    [InlineData("uses: [a]", "[a]", "'uses' must be a string")]
    [InlineData("uses: liquid, with: [a]", "[a]", "'with' must be a mapping")]
    [InlineData("uses: liquid, incremental: maybe", "maybe", "'incremental' must be true or false")]
    [InlineData("uses: liquid, match: { a: b }", "{ a: b }", "'match' must be a glob or a list of globs")]
    public void WrongType(string fields, string value, string message)
    {
        var line = $"  render: {{ inputs: \"**\", {fields} }}";

        AssertError($"steps:\n{line}\n", message, 2, line.LastIndexOf(value, StringComparison.Ordinal) + 1);
    }

    [Fact]
    public void WrongEnumValue()
    {
        AssertError("""
            steps:
              render: { inputs: "**", run: tool, mode: parallel }
            """, "'mode' must be 'filter', 'batch' or 'worker'", 2, 44);
    }

    [Fact]
    public void EnvValuesMustBeStrings()
    {
        AssertError("""
            steps:
              render: { inputs: "**", run: tool, env: { A: [1] } }
            """, "'env' must be a mapping of names to strings", 2, 48);
    }

    [Fact]
    public void UsesAndRun()
    {
        AssertError("""
            steps:
              x: { inputs: "**", uses: write, run: tool }
            """, "step 'x' cannot have both 'uses' and 'run'", 2, 3);
    }

    [Fact]
    public void NeitherUsesNorRun()
    {
        AssertError("""
            steps:
              x: { inputs: "**" }
            """, "step 'x' needs 'uses' or 'run'", 2, 3);
    }

    [Fact]
    public void EmptyStepNeedsUsesOrRun()
    {
        AssertError("steps:\n  x:\n", "step 'x' needs 'uses' or 'run'", 2, 3);
    }

    [Theory]
    [InlineData("mode: filter")]
    [InlineData("outputs: a/**")]
    [InlineData("env: { A: b }")]
    [InlineData("cwd: tools")]
    [InlineData("timeout: 30s")]
    [InlineData("stderr: warn")]
    [InlineData("shell: false")]
    [InlineData("version: \"2\"")]
    [InlineData("sources: a.mjs")]
    public void CommandFieldWithoutRun(string field)
    {
        var name = field[..field.IndexOf(':')];

        AssertError($"steps:\n  x:\n    inputs: \"**\"\n    uses: write\n    {field}\n", $"'{name}' is only valid with 'run'", 5, 5);
    }

    [Fact]
    public void WithWithoutUses()
    {
        AssertError("""
            steps:
              x:
                inputs: "**"
                run: tool
                with: { a: 1 }
            """, "'with' is only valid with 'uses' or with 'mode: worker'", 5, 5);
    }

    [Fact]
    public void WithOnWorkerIsValid()
    {
        var step = Load("""
            steps:
              x: { inputs: "**", run: tool, mode: worker, with: { concurrency: 4 } }
            """).Workflow!.Steps.Single();

        Assert.Equal(CommandMode.Worker, step.Mode);
        Assert.Equal(4, step.With["concurrency"]!.GetValue<int>());
    }

    [Fact]
    public void InvalidGlob()
    {
        AssertError("""
            steps:
              x: { inputs: ["**/*.md", "content/["], uses: write }
            """, "invalid pattern 'content/[': unterminated character class", 2, 28);
    }

    [Fact]
    public void InvalidDuration()
    {
        AssertError("""
            steps:
              x: { inputs: "**", run: tool, timeout: 5 minutes }
            """, "invalid timeout '5 minutes'; use 30s, 5m or 1h", 2, 42);
    }

    [Fact]
    public void DuplicateKey()
    {
        AssertError("""
            steps:
              x: { inputs: "**", uses: write }
              x: { inputs: "**", uses: write }
            """, "duplicate key 'x'", 3, 3);
    }

    [Fact]
    public void YamlSyntaxError()
    {
        AssertError("steps:\n  x: [a\n", "while parsing a flow sequence, did not find expected ',' or ']'", 3, 1);
    }

    [Fact]
    public void SiteUnknownKey()
    {
        AssertError("""
            site:
              contnt: docs
            """, "unknown key 'contnt' in 'site'; expected 'content', 'data', 'includes', 'output', 'url' or 'parallelism'", 2, 3);
    }

    [Theory]
    [InlineData("parallelism: 0", "'parallelism' must be a positive integer", 16)]
    [InlineData("parallelism: many", "'parallelism' must be a positive integer", 16)]
    [InlineData("content: ../docs", "'content' must be a folder inside the source folder", 12)]
    [InlineData("url: [a]", "'url' must be a string", 8)]
    public void InvalidSiteValue(string setting, string message, int column)
    {
        AssertError($"site:\n  {setting}\n", message, 2, column);
    }

    [Fact]
    public void EveryErrorIsReportedInFileOrder()
    {
        var errors = Errors("""
            jobs: 1
            steps:
              a: { inputs: "**", mode: batch, uses: write }
              B: { inputs: "**", uses: write }
            """);

        Assert.Equal(
            [
                "douze.yml(1,1): error: unknown key 'jobs'; expected 'site' or 'steps'",
                "douze.yml(3,22): error: 'mode' is only valid with 'run'",
                "douze.yml(4,3): error: invalid step name 'B': use lowercase letters, digits and dashes",
            ],
            errors.Select(x => x.ToString()));
    }

    // Graph rules (section 4.5), reported at the step's name.

    [Fact]
    public void UnknownNeeds()
    {
        AssertGraphError("""
            steps:
              markdown: { inputs: "**", uses: markdown }
              render: { needs: markdwn, uses: liquid }
            """, "step 'render' needs unknown step 'markdwn'; did you mean 'markdown'?", 3, 3);
    }

    [Fact]
    public void Cycle()
    {
        AssertGraphError("""
            steps:
              pages: { inputs: "**", uses: frontmatter }
              a: { needs: [c, pages], uses: markdown }
              b: { needs: a, uses: markdown }
              c: { needs: b, uses: markdown }
            """, "steps form a cycle: a -> b -> c -> a", 3, 3);
    }

    [Fact]
    public void NoNeedsAndNoInputs()
    {
        AssertGraphError("""
            steps:
              write: { uses: write }
            """, "step 'write' has no 'needs' and no 'inputs', so nothing enters it", 2, 3);
    }

    [Fact]
    public void UnknownUses()
    {
        AssertGraphError("""
            steps:
              pages: { inputs: "**", uses: frontmater }
            """, "unknown built-in step 'frontmater'; did you mean 'frontmatter'?", 2, 3);
    }

    [Fact]
    public void GroupByNotSupported()
    {
        AssertGraphError("""
            steps:
              markdown: { inputs: "**", uses: markdown, group-by: dir }
            """, "step 'markdown' does not support 'group-by'", 2, 3);
    }

    [Fact]
    public void UnknownWithOption()
    {
        AssertGraphError("""
            steps:
              render: { inputs: "**", uses: liquid, with: { templte: a.liquid } }
            """, "unknown option 'templte' for step 'render'; did you mean 'template'?", 2, 3);
    }

    [Fact]
    public void MissingRequiredWithOption()
    {
        AssertGraphError("""
            steps:
              api: { inputs: "**", uses: liquid, with: { template: api.liquid } }
            """, "step 'api' uses 'liquid' with 'template' but 'permalink' is missing", 2, 3);
    }

    [Fact]
    public void OutputsOnANonBatchCommand()
    {
        var workflow = Load("""
            steps:
              x: { inputs: "**", run: tool, mode: filter, outputs: "*.png" }
            """).Workflow!;

        // The fake factory stands in for the command steps.
        var error = Assert.Single(Assert.Throws<WorkflowValidationException>(() => WorkflowGraph.Build(workflow, d => new FakeMapStep(d))).Errors);

        Assert.Equal("douze.yml(2,3): error: 'outputs' is only valid with 'mode: batch'", error.ToString());
    }

    [Fact]
    public void NoWriteStepIsAWarning()
    {
        var graph = WorkflowGraph.Build(Load("""
            steps:
              pages: { inputs: "**", uses: frontmatter }
            """).Workflow!);

        Assert.Equal(["no 'write' step: nothing will be written to the output folder"], graph.Warnings);
    }

    // Loading.

    [Fact]
    public void EmptyFileUsesTheDefaults()
    {
        var file = Load("# nothing yet\n");

        Assert.Equal(SiteSettings.None, file.Site);
        Assert.Null(file.Workflow);
    }

    [Fact]
    public void StepsAbsentLeavesTheWorkflowToTheDefault()
    {
        var file = Load("site:\n  url: https://example.com\n");

        Assert.Null(file.Workflow);
        Assert.Equal("https://example.com", file.Site.Url);
    }

    [Fact]
    public void SiteValues()
    {
        var site = Load("""
            site:
              content: docs
              data: /data/
              includes: .
              output: ../public
              url: https://example.com
              parallelism: 8
            """).Site;

        Assert.Equal(new FPath("/docs/"), site.ContentFolder);
        Assert.Equal(new FPath("/data/"), site.DataFolder);
        Assert.Equal(FPath.Root, site.IncludesFolder);
        Assert.Equal("../public", site.Output);
        Assert.Equal("https://example.com", site.Url);
        Assert.Equal(8, site.Parallelism);
    }

    [Fact]
    public void StepFieldsAndLocations()
    {
        var steps = Load("""
            steps:
              pages: { inputs: "content/**", uses: frontmatter }
              render:
                needs: pages
                uses: liquid
                match: ["**/*.md", "!drafts/**"]
            """).Workflow!.Steps;

        Assert.Equal(["pages", "render"], steps.Select(x => x.Name));
        Assert.Equal(["content/**"], steps[0].Inputs);
        Assert.Equal(["pages"], steps[1].Needs);
        Assert.Equal(["**/*.md", "!drafts/**"], steps[1].Match);
        Assert.Equal(new SourceLocation("douze.yml", 3, 3), steps[1].Location);
    }

    [Fact]
    public void WithValuesAreTypedLikeDataFiles()
    {
        var with = Load("""
            steps:
              x: { inputs: "**", uses: liquid, with: { a: 1, b: 1.5, c: true, d: "1", e: null, f: [x, 2], g: { h: i } } }
            """).Workflow!.Steps[0].With;

        Assert.Equal(1, with["a"]!.GetValue<int>());
        Assert.Equal(1.5m, with["b"]!.GetValue<decimal>());
        Assert.True(with["c"]!.GetValue<bool>());
        Assert.Equal("1", with["d"]!.GetValue<string>());
        Assert.True(with.ContainsKey("e"));
        Assert.Null(with["e"]);
        Assert.Equal(2, ((NodeArray)with["f"]!).Count);
        Assert.Equal("i", ((NodeObject)with["g"]!)["h"]!.GetValue<string>());
    }

    [Fact]
    public void ApiReferenceExampleLoads()
    {
        var workflow = WorkflowLoader.Load(File.ReadAllText(Path.Combine(Fixtures, "api-reference.yml")), "api-reference.yml").Workflow!;

        Assert.Equal(13, workflow.Steps.Length);

        var csharp = workflow.Steps.Single(x => x.Name == "csharp");
        Assert.Equal("file", csharp.GroupBy);
        Assert.Equal(["packages", "site-data"], csharp.Needs);
        Assert.Equal("api/csharp-package.liquid", csharp.With["template"]!.GetValue<string>());

        var og = workflow.Steps.Single(x => x.Name == "og-images");
        Assert.Equal(CommandMode.Batch, og.Mode);
        Assert.Equal(["og/*.png"], og.Outputs);
        Assert.Equal(["scripts/og.mjs"], og.Sources);
        Assert.Equal("api-reference.yml", workflow.SourceFile);
    }

    [Fact]
    public async Task LoadAsyncKeepsTheFullPath()
    {
        var path = Path.Combine(Fixtures, "api-reference.yml");

        var file = await WorkflowLoader.LoadAsync(path, "api-reference.yml", TestContext.Current.CancellationToken);

        Assert.Equal(Path.GetFullPath(path), file.Workflow!.SourceFile);
        Assert.Equal("api-reference.yml", file.Workflow.Steps[0].Location!.Value.File);
    }

    [Fact]
    public void AllFields()
    {
        var steps = WorkflowLoader.Load(File.ReadAllText(Path.Combine(Fixtures, "all-fields.yml")), "all-fields.yml").Workflow!.Steps;

        var convert = steps.Single(x => x.Name == "convert");
        Assert.Equal("node convert.mjs --in {{ inputs }}\necho \"done\"\n", convert.Run);
        Assert.Equal(CommandMode.Worker, convert.Mode);
        Assert.Equal("2", convert.Version);
        Assert.Equal("1", convert.Env["NODE_NO_WARNINGS"]);
        Assert.Equal("tools", convert.WorkingDirectory);
        Assert.Equal(TimeSpan.FromSeconds(90), convert.Timeout);
        Assert.Equal(StderrPolicy.Warn, convert.Stderr);
        Assert.False(convert.Shell);
        Assert.False(steps.Single(x => x.Name == "pages").Incremental);
    }

    [Theory]
    [InlineData("all-fields.yml")]
    [InlineData("api-reference.yml")]
    [InlineData("two-pipelines/douze.yml")]
    public void PrintedWorkflowLoadsBackTheSame(string fixture)
    {
        var first = WorkflowLoader.Load(File.ReadAllText(Path.Combine(Fixtures, fixture)), "douze.yml").Workflow!;
        var yaml = WorkflowSchema.ToYaml(first);
        var second = WorkflowLoader.Load(yaml, "printed.yml").Workflow!;

        Assert.Equal(first.Steps.Select(x => x.Name), second.Steps.Select(x => x.Name));
        Assert.Equal(first.Steps.Select(x => string.Join(",", x.Needs)), second.Steps.Select(x => string.Join(",", x.Needs)));
        Assert.Equal(first.Steps.Select(x => x.ConfigHash), second.Steps.Select(x => x.ConfigHash));
        Assert.Equal(yaml, WorkflowSchema.ToYaml(second));
    }

    [Fact]
    public void PrintedWorkflowOfTheGraph()
    {
        var workflow = Load("""
            steps:
              pages: { uses: frontmatter, inputs: "content/**" }
              write: { needs: pages, uses: write }
            """).Workflow!;

        Assert.Equal("""
            steps:
              pages: { inputs: "content/**", uses: frontmatter }
              write: { needs: pages, uses: write }

            """, WorkflowGraph.Build(workflow).ToYaml());
    }

    [Fact]
    public void EmptyStepsPrintsAnEmptyMapping()
    {
        var workflow = Load("steps: {}\n").Workflow!;

        Assert.Empty(workflow.Steps);
        Assert.Equal("steps: {}\n", WorkflowSchema.ToYaml(workflow));
        Assert.NotNull(Load(WorkflowSchema.ToYaml(workflow)).Workflow);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("_content/a b.md", "_content/a b.md")]
    [InlineData("true", "\"true\"")]
    [InlineData("12", "\"12\"")]
    [InlineData("1.5", "\"1.5\"")]
    [InlineData("null", "\"null\"")]
    [InlineData("", "\"\"")]
    [InlineData("-x", "\"-x\"")]
    [InlineData("a ", "\"a \"")]
    [InlineData("**/*.md", "\"**/*.md\"")]
    [InlineData("a: b", "\"a: b\"")]
    [InlineData("{{ x }}", "\"{{ x }}\"")]
    [InlineData("say \"hi\"\\\n\t", "\"say \\\"hi\\\"\\\\\\n\\t\"")]
    public void ScalarsReadBackAsStrings(string value, string expected)
    {
        var scalar = WorkflowSchema.Scalar(value);

        Assert.Equal(expected, scalar);

        var with = Load($"steps:\n  x: {{ inputs: \"**\", uses: write, with: {{ v: {scalar} }} }}\n").Workflow!.Steps[0].With;
        Assert.Equal(value, with["v"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("500ms", 500)]
    [InlineData("30s", 30_000)]
    [InlineData("90s", 90_000)]
    [InlineData("5m", 300_000)]
    [InlineData("1h", 3_600_000)]
    [InlineData("0s", null)]
    [InlineData("5 minutes", null)]
    [InlineData("5", null)]
    [InlineData("1.5h", null)]
    [InlineData("-1s", null)]
    [InlineData("1d", null)]
    [InlineData("5M", null)]
    public void Durations(string text, int? milliseconds)
    {
        var valid = WorkflowSchema.TryParseDuration(text, out var duration);

        Assert.Equal(milliseconds.HasValue, valid);

        if (milliseconds.HasValue)
        {
            Assert.Equal(TimeSpan.FromMilliseconds(milliseconds.Value), duration);
            Assert.Equal(text, WorkflowSchema.FormatDuration(duration));
        }
    }

    [Theory]
    [InlineData(120_000, "2m")]
    [InlineData(7_200_000, "2h")]
    [InlineData(1_500, "1500ms")]
    public void DurationsUseTheLargestUnit(int milliseconds, string expected)
    {
        Assert.Equal(expected, WorkflowSchema.FormatDuration(TimeSpan.FromMilliseconds(milliseconds)));
    }
}
