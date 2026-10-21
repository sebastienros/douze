using System.Collections.Immutable;
using Douze.Workflow;
using Douze.Workflow.Commands;
using Fluid;
using Xunit;

namespace Douze.Tests.Workflow.Commands;

public class CommandTemplateTests
{
    private static readonly TemplateOptions Options = new TemplateOptionsBuilder().Build();

    private static CommandTemplate Parse(string run, CommandMode mode = CommandMode.Batch, bool shell = true, params (string Key, string Value)[] env)
    {
        return CommandTemplate.Parse(new StepDefinition
        {
            Name = "cmd",
            Run = run,
            Mode = mode,
            Shell = shell,
            Env = env.ToImmutableDictionary(x => x.Key, x => x.Value),
        });
    }

    private static string Render(CommandTemplate template, CommandModel model)
    {
        return template.RenderRunAsync(model, Options, TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public void Expansion()
    {
        var template = Parse("tool {{ step }} --cwd {{ cwd }} --site {{ site }} {{ inputs }} {{ outputs }}");

        Assert.True(template.UsesInputs);
        Assert.True(template.UsesOutputs);
        Assert.Equal(
            "tool cmd --cwd /src --site /src/_site /tmp/in /tmp/out",
            Render(template, new CommandModel("cmd", "/src", "/src/_site", "/tmp/in", "/tmp/out")));

        // Values the mode does not provide render empty, like any unknown name in Liquid.
        var plain = Parse("tool {{ step }}{{ site }}");
        Assert.False(plain.UsesInputs);
        Assert.False(plain.UsesOutputs);
        Assert.Equal("tool cmd", Render(plain, new CommandModel("cmd", "/src", null)));
    }

    [Fact]
    public void Quoting()
    {
        var model = new CommandModel("cmd", "/my src", "/it's here", "/plain/in");

        Assert.Equal(
            OperatingSystem.IsWindows() ? "tool \"/my src\" \"/it's here\" /plain/in" : "tool '/my src' '/it'\\''s here' /plain/in",
            Render(Parse("tool {{ cwd }} {{ site }} {{ inputs }}"), model));

        Assert.Equal("tool '/my src' \"/it's here\" /plain/in", Render(Parse("tool {{ cwd }} {{ site }} {{ inputs }}", shell: false), model));

        // A filter returns a plain string, written as it is.
        Assert.Equal("tool /my src/x", Render(Parse("tool {{ cwd | append: '/x' }}"), model));

        // Splitting what was quoted for 'shell: false' gives the value back.
        foreach (var value in (string[])["plain", "with space", "it's", "a \"quoted\" it's", "", @"C:\Program Files\x"])
        {
            Assert.Equal(["tool", value], ProcessRunner.Split("tool " + CommandTemplate.Quote(value, QuoteStyle.Split)));
        }

        Assert.Equal(["a", "b c", "d\"e", @"C:\x\"], ProcessRunner.Split(@"a 'b c' ""d\""e"" C:\x\"));
        Assert.Throws<DouzeException>(() => ProcessRunner.Split("tool 'open"));
    }

    [Fact]
    public void InputsAndOutputsOnlyInBatchMode()
    {
        var filter = Assert.Throws<WorkflowValidationException>(() => Parse("tool {{ inputs }} {{ outputs | append: '/' }}", CommandMode.Filter));
        Assert.Equal(
            ["'{{ inputs }}' is only valid with 'mode: batch' (step 'cmd')", "'{{ outputs }}' is only valid with 'mode: batch' (step 'cmd')"],
            filter.Errors.Select(x => x.Message));

        var worker = Assert.Throws<WorkflowValidationException>(() => Parse("tool", CommandMode.Worker, env: ("OUT", "{{ outputs }}")));
        Assert.Equal("'{{ outputs }}' is only valid with 'mode: batch' (step 'cmd')", worker.Errors.Single().Message);

        var file = Assert.Throws<WorkflowValidationException>(() => Parse("tool {% if file.ext == '.md' %}x{% endif %}", CommandMode.Batch));
        Assert.Equal("'{{ file }}' is only valid with 'mode: filter' (step 'cmd')", file.Errors.Single().Message);

        var invalid = Assert.Throws<WorkflowValidationException>(() => Parse("tool {{ step"));
        Assert.StartsWith("invalid template in 'run' of step 'cmd': ", invalid.Errors.Single().Message);

        // In batch mode, an env value referencing inputs is enough to materialize them.
        Assert.True(Parse("tool", env: ("IN", "{{ inputs }}")).UsesInputs);
    }

    [Fact]
    public async Task EnvExpansion()
    {
        var template = Parse("tool", CommandMode.Filter, env: [("B", "{{ file.name }} in {{ cwd }}"), ("A", "{{ step }}")]);

        var env = await template.RenderEnvAsync(new CommandModel("cmd", "/my src", null, File: "/a b/c.md"), Options, TestContext.Current.CancellationToken);

        // Sorted by name, never quoted.
        Assert.Equal([new("A", "cmd"), new("B", "c.md in /my src")], env);
    }

    [Fact]
    public void FileModel()
    {
        var template = Parse("{{ file.path }}|{{ file.dir }}|{{ file.name }}|{{ file.ext }}|{{ file.name | upcase }}", CommandMode.Filter, shell: false);

        Assert.Equal(
            "'/docs/my page.md'|/docs/|'my page.md'|.md|MY PAGE.MD",
            Render(template, new CommandModel("cmd", "/src", null, File: "/docs/my page.md")));
    }
}
