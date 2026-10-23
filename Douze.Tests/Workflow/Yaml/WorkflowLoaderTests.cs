using Douze.FileSystems;
using Douze.Markdown;
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
            """, "unknown key 'jobs'; expected 'site', 'vars', 'markdown' or 'steps'", 3, 1);
    }

    [Fact]
    public void DisabledStepsAreLeftOut()
    {
        // The steps that need a disabled step need what it needs, through several disabled steps.
        var steps = Load("""
            steps:
              data: { inputs: "data/**", uses: data, with: { scope: site } }
              pages: { inputs: "**", needs: data, uses: frontmatter }
              highlight: { needs: pages, uses: highlight }
              worker: { needs: highlight, run: node worker.mjs, mode: worker, enabled: false }
              other: { needs: worker, run: node other.mjs, enabled: false }
              write: { needs: [other, data], uses: write, enabled: true }
            """).Workflow!.Steps;

        Assert.Equal(["data", "pages", "highlight", "write"], steps.Select(x => x.Name));
        Assert.Equal(["highlight", "data"], steps[^1].Needs);
    }

    [Fact]
    public void EnabledExpressionsSeeTheEnvironment()
    {
        var environment = new WorkflowEnvironment("windows", "arm64", new Dictionary<string, string> { ["Douze_OG"] = "1" }, watch: true);

        var steps = WorkflowLoader.Load("""
            steps:
              pages: { inputs: "**", uses: frontmatter }
              og: { needs: pages, uses: social-image, with: { template: og.svg, output: og.png }, enabled: "os != 'windows'" }
              arm: { needs: og, uses: markdown, enabled: "arch == 'arm64' and watch" }
              env: { needs: arm, uses: layout, enabled: "env.DOUZE_OG == '1'" }
              missing: { needs: env, uses: liquid, enabled: "env.MISSING" }
              write: { needs: missing, uses: write }
            """, "douze.yml", environment: environment).Workflow!.Steps;

        // Variable names are case insensitive on Windows; a missing variable is nil.
        Assert.Equal(["pages", "arm", "env", "write"], steps.Select(x => x.Name));
        Assert.Equal(["env"], steps[^1].Needs);
        Assert.Equal(["pages"], steps[1].Needs);
    }

    [Fact]
    public void VarsAreSetByTheEnvironmentThenTheCommandLine()
    {
        var env = new Dictionary<string, string> { ["DOUZE_VARS__OG"] = "false", ["DOUZE_VARS__label"] = "env", ["SECRET"] = "s3cret" };
        var environment = new WorkflowEnvironment("linux", "x64", env, watch: false,
            [VarOverride.FromValue("label", "cli"), VarOverride.FromVariable("token", "SECRET"), VarOverride.FromVariable("count", "UNSET")]);

        var file = WorkflowLoader.Load("""
            steps:
              pages: { inputs: "**", uses: frontmatter }
              og: { needs: pages, uses: markdown, enabled: "vars.og" }
              write: { needs: og, uses: write }
            vars:
              og: true
              label: default
              token: ~
              count: 1
            """, "douze.yml", environment: environment);

        // The section can follow the steps; an unset variable of --env leaves the value as it is.
        Assert.Equal(["pages", "write"], file.Workflow!.Steps.Select(x => x.Name));
        Assert.Equal(new NodeObject { ["og"] = false, ["label"] = "cli", ["token"] = "s3cret", ["count"] = 1 }, file.Vars);
    }

    [Fact]
    public void VarsErrors()
    {
        var env = new Dictionary<string, string> { ["NUM"] = "abc" };
        var environment = new WorkflowEnvironment("linux", "x64", env, watch: false,
            [VarOverride.FromValue("ogg", "1"), VarOverride.FromVariable("count", "NUM"), VarOverride.FromValue("og", "nope"), VarOverride.FromValue("list", "x")]);

        var error = Assert.Throws<WorkflowValidationException>(() => WorkflowLoader.Load("""
            vars: { og: true, count: 1, list: [a, b] }
            """, "douze.yml", environment: environment));

        // The value of an environment variable mapped by --env is not shown.
        Assert.Equal(
        [
            "error: unknown variable 'ogg' in '--var ogg'; declare it in 'vars'; did you mean 'og'?",
            "error: variable 'count' must be an integer; the value of '--env count=NUM' is not",
            "error: variable 'og' must be true or false, not 'nope' ('--var og')",
            "error: variable 'list' is a list and cannot be set by '--var list'",
        ], error.Errors.Select(x => x.ToString()));
    }

    [Fact]
    public void InvalidEnabledExpression()
    {
        var error = Assert.Single(Errors("""
            steps:
              write: { inputs: "**", uses: write, enabled: "os ==" }
            """));

        Assert.StartsWith("'enabled': invalid expression 'os =='", error.Message);
        Assert.Equal(new SourceLocation("douze.yml", 2, 48), error.Location);
    }

    [Fact]
    public void RootMustBeAMapping()
    {
        AssertError("- a\n- b\n", "a workflow file must be a mapping with 'site', 'vars', 'markdown' and 'steps'", 1, 1);
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
            """, "unknown key 'contnt' in 'site'; expected 'content', 'data', 'includes', 'shortcodes', 'output', 'url', 'parallelism' or 'locales'", 2, 3);
    }

    [Theory]
    [InlineData("parallelism: 0", "'parallelism' must be a positive integer", 16)]
    [InlineData("parallelism: many", "'parallelism' must be a positive integer", 16)]
    [InlineData("content: ../docs", "'content' must be a folder inside the source folder", 12)]
    [InlineData("shortcodes: ../components", "'shortcodes' must be a folder inside the source folder", 15)]
    [InlineData("shortcodes: [a]", "'shortcodes' must be a string", 15)]
    [InlineData("url: [a]", "'url' must be a string", 8)]
    public void InvalidSiteValue(string setting, string message, int column)
    {
        AssertError($"site:\n  {setting}\n", message, 2, column);
    }

    // The markdown section.

    [Fact]
    public void MarkdownAbsentOrEmpty()
    {
        Assert.Null(Load("site:\n  url: https://example.com\n").Markdown);
        Assert.Null(Load("markdown:\n").Markdown);
        Assert.Equal(MarkdownOptions.DefaultExtensions, Load("markdown: {}\n").Markdown!.Extensions);
    }

    [Fact]
    public void MarkdownValues()
    {
        var markdown = Load("""
            markdown:
              extensions: [pipetables, alerts]
              aside: { tag: div, class: callout, kinds: [note, warning], titles: { warning: Attention }, icons: { note: "<svg/>" }, titles-from: i18n }
              links: false
              code: { class-prefix: lang-, passthrough: [mermaid, dot] }
              images: { widths: [1280, 640], formats: webp, quality: 80, output: assets/images, loading: lazy }
            """).Markdown!;

        Assert.Equal(["pipetables", "alerts"], markdown.Extensions);
        Assert.Equal("div", markdown.Aside.Tag);
        Assert.Equal("callout", markdown.Aside.Class);
        Assert.Equal(["note", "warning"], markdown.Aside.Kinds);
        Assert.Equal("Attention", markdown.Aside.Title("warning"));
        Assert.Equal("Note", markdown.Aside.Title("note"));
        Assert.Equal("<svg/>", markdown.Aside.Icons["note"]);
        Assert.Equal("i18n", markdown.Aside.TitlesFrom);
        Assert.False(markdown.RewriteLinks);
        Assert.Equal("lang-", markdown.CodeClassPrefix);
        Assert.Equal(["mermaid", "dot"], markdown.PassthroughLanguages);
        Assert.Equal([640, 1280], markdown.Images.Widths);
        Assert.Equal(["webp"], markdown.Images.Formats);
        Assert.Equal(80, markdown.Images.Quality);
        Assert.Equal("assets/images", markdown.Images.Output);
        Assert.Equal("lazy", markdown.Images.Loading);
    }

    [Fact]
    public void MarkdownUnknownKey()
    {
        AssertError("markdown:\n  extension: [pipetables]\n", "unknown key 'extension' in 'markdown'; expected 'extensions', 'aside', 'links', 'code' or 'images'", 2, 3);
    }

    [Theory]
    [InlineData("markdown: [a]", "'markdown' must be a mapping", 1, 11)]
    [InlineData("markdown:\n  extensions: [pipetables, tabels]", "unknown Markdown extension 'tabels'; did you mean 'pipetables'?", 2, 28)]
    [InlineData("markdown:\n  extensions: [autoidentifiers]", "use the headings step for heading ids", 2, 16)]
    [InlineData("markdown:\n  extensions: { a: b }", "'extensions' must be a string or a list of strings", 2, 15)]
    [InlineData("markdown:\n  links: maybe", "'links' must be true or false", 2, 10)]
    [InlineData("markdown:\n  aside: { color: red }", "unknown key 'color' in 'markdown.aside'; expected 'tag', 'class', 'kinds', 'titles', 'icons' or 'titles-from'", 2, 12)]
    [InlineData("markdown:\n  aside: { tag: \"<b>\" }", "'tag' must be an HTML element name", 2, 17)]
    [InlineData("markdown:\n  aside: { titles: { hint: Hint } }", "unknown aside kind 'hint'; expected 'note', 'tip', 'caution' or 'danger'", 2, 22)]
    [InlineData("markdown:\n  aside: { titles: [a] }", "'titles' must be a mapping of kinds to strings", 2, 20)]
    [InlineData("markdown:\n  code: { prefix: x }", "unknown key 'prefix' in 'markdown.code'; expected 'class-prefix' or 'passthrough'", 2, 11)]
    [InlineData("markdown:\n  images: { quality: 101 }", "'quality' must be an integer from 1 to 100", 2, 22)]
    [InlineData("markdown:\n  images: { widths: [640, big] }", "'widths' must be a list of positive integers", 2, 27)]
    [InlineData("markdown:\n  images: { loading: soon }", "'loading' must be 'lazy' or 'eager'", 2, 22)]
    public void InvalidMarkdownValue(string yaml, string message, int line, int column)
    {
        AssertError(yaml + "\n", message, line, column);
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
                "douze.yml(1,1): error: unknown key 'jobs'; expected 'site', 'vars', 'markdown' or 'steps'",
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
              shortcodes: templates/components
              output: ../public
              url: https://example.com
              parallelism: 8
            """).Site;

        Assert.Equal(new FPath("/docs/"), site.ContentFolder);
        Assert.Equal(new FPath("/data/"), site.DataFolder);
        Assert.Equal(FPath.Root, site.IncludesFolder);
        Assert.Equal(new FPath("/templates/components/"), site.ShortcodesFolder);
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

        var file = await WorkflowLoader.LoadAsync(path, "api-reference.yml", ct: TestContext.Current.CancellationToken);

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

    // site.locales

    [Fact]
    public void Locales()
    {
        var locales = Load("""
            site:
              locales:
                default: en
                root: docs
                fallback: true
                strings: strings
                languages:
                  en: { label: English }
                  ja: { label: 日本語, lang: ja }
                  ar: { lang: ar, dir: rtl }
                  fr:
            """).Site.Locales!;

        Assert.True(locales.IsEnabled);
        Assert.Equal(new Locale("en", "English", "en", "ltr", ""), locales.Default);
        Assert.Equal(
            [new Locale("ja", "日本語", "ja", "ltr", "/ja"), new Locale("ar", "ar", "ar", "rtl", "/ar"), new Locale("fr", "fr", "fr", "ltr", "/fr")],
            locales.Languages);
        Assert.True(locales.Fallback);
        Assert.Equal("strings", locales.Strings);
        Assert.Equal(new FPath("/content/docs/"), locales.WithContentFolder("/content/").Root);
        Assert.Equal(new FPath("/content/"), Load("site:\n  locales: { default: en }\n").Site.Locales!.WithContentFolder("/content/").Root);
    }

    [Fact]
    public void LocalesDefaults()
    {
        var locales = Load("site:\n  locales: { default: en }\n").Site.Locales!;

        Assert.Equal(new Locale("en", "en", "en", "ltr", ""), locales.Default);
        Assert.Empty(locales.Languages);
        Assert.False(locales.Fallback);
        Assert.Equal("i18n", locales.Strings);
        Assert.Same(LocaleSet.None, SiteSettings.None.Locales ?? LocaleSet.None);
    }

    [Theory]
    [InlineData("locales: [en]", "'locales' must be a mapping", 2, 12)]
    [InlineData("locales: { root: docs }", "'site.locales' needs 'default', the locale whose pages have no url prefix", 2, 12)]
    [InlineData("locales: { default: en, langs: {} }", "unknown key 'langs' in 'site.locales'; expected 'default', 'root', 'fallback', 'languages' or 'strings'", 2, 27)]
    [InlineData("locales: { default: en/us }", "'default' must be a locale folder name", 2, 23)]
    [InlineData("locales: { default: en, root: ../docs }", "'root' must be a folder inside the content folder", 2, 33)]
    [InlineData("locales: { default: en, fallback: maybe }", "'fallback' must be true or false", 2, 37)]
    [InlineData("locales: { default: en, languages: [ja] }", "'languages' must be a mapping of locale folders to settings", 2, 38)]
    [InlineData("locales: { default: en, languages: { ja: { label: J, rtl: true } } }", "unknown key 'rtl' in 'site.locales.languages.ja'; expected 'label', 'lang' or 'dir'", 2, 56)]
    [InlineData("locales: { default: en, languages: { ar: { dir: right } } }", "'dir' must be 'ltr' or 'rtl'", 2, 51)]
    [InlineData("locales: { default: en, languages: { ja: J } }", "the settings of locale 'ja' must be a mapping of 'label', 'lang' and 'dir'", 2, 44)]
    public void InvalidLocales(string setting, string message, int line, int column)
    {
        AssertError($"site:\n  {setting}\n", message, line, column);
    }
}
