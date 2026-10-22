using System.Text.RegularExpressions;
using Douze.Progress;
using Douze.Tests.Workflow;
using Douze.Workflow;
using Spectre.Console.Testing;
using Xunit;

namespace Douze.Tests.Progress;

public class SpectreProgressViewTests
{
    private static TestConsole Console(bool live = false)
    {
        var console = new TestConsole().Width(120);
        console.Profile.Capabilities.Unicode = true;

        if (live)
        {
            console.Interactive();
            console.EmitAnsiSequences = true;
        }

        return console;
    }

    private static string[] Lines(TestConsole console)
    {
        // Without the escape sequences of the live display; a carriage return starts the line over, except in the line
        // ends of the console on Windows.
        var text = Regex.Replace(console.Output, @"\x1b\[[0-9;?]*[A-Za-z]", "").Replace("\r\n", "\n", StringComparison.Ordinal);
        return text.Split('\n').Select(x => x[(x.LastIndexOf('\r') + 1)..].TrimEnd()).Where(x => x.Length > 0).ToArray();
    }

    [Fact]
    public async Task RunningRows()
    {
        var gate = new TaskCompletionSource();
        var site = await new FakeSite().WithFilesAsync("/a.md", "/b.md");
        var view = new SpectreProgressView(Console());

        var workflow = new FakeWorkflow();
        workflow.Add("markdown", d => new FakeMapStep(d) { Before = (_, ct) => gate.Task.WaitAsync(ct) }, inputs: ["**"]);

        var build = site.BuildAsync(workflow.Build(), view: view);

        string table;

        while (true)
        {
            var rendered = Console();
            rendered.Write(view.RunningTable());
            table = rendered.Output;

            // The total is known once the input of the step is complete.
            if (table.Contains("0/2", StringComparison.Ordinal))
            {
                break;
            }

            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        gate.SetResult();
        await build;

        Assert.Matches(@"markdown\s+map\s+0/2 ─+ 0\.\d\d s", table);
        Assert.Contains("0/1 steps done", table);
    }

    [Fact]
    public async Task CompletionLines()
    {
        var site = await new FakeSite().WithFilesAsync("/a.md");
        var console = Console();

        await site.BuildAsync(new FakeWorkflow()
            .Add("markdown", d => new FakeMapStep(d), inputs: ["**"])
            .Add("broken", d => new FakeMapStep(d) { FailWhen = _ => true }, needs: ["markdown"])
            .Add("og-images", d => new FakeMapStep(d), needs: ["broken"])
            .Build(), view: new SpectreProgressView(console));

        var lines = Lines(console);

        Assert.Equal("warning: no 'write' step: nothing will be written to the output folder", lines[0]);
        Assert.Matches(@"^✓ markdown         1 file     0\.\d\d s$", lines[1]);
        Assert.Equal("✗ broken      error: broken: /a.md: failed on /a.md", lines[2]);
        Assert.Equal("- og-images   skipped", lines[3]);
    }

    [Fact]
    public async Task Summary()
    {
        var site = await new FakeSite().WithFilesAsync("/a.md", "/b.md");
        var console = Console();

        await site.BuildAsync(new FakeWorkflow()
            .Add("markdown", d => new FakeMapStep(d) { Delay = TimeSpan.FromMilliseconds(20) }, inputs: ["**"])
            .Write("write", "markdown")
            .Build(), view: new SpectreProgressView(console));

        var lines = Lines(console);
        var header = Array.FindIndex(lines, x => x.TrimStart().StartsWith("Step", StringComparison.Ordinal));

        Assert.Matches(@"Step\s+State\s+In\s+Out\s+Units\s+Cached\s+Time\s+0\s+0\.\d\d s", lines[header]);
        Assert.Matches(@"markdown\s+succeeded\s+2\s+2\s+2\s+0\s+0\.\d\d s\s+█", lines[header + 2]);
        Assert.Matches(@"write\s+succeeded\s+2\s+2\s+2\s+0\s+0\.\d\d s\s+ *[█▏▎▍▌▋▊▉]", lines[header + 3]);
        Assert.StartsWith("Build succeeded: 2 steps, 2 files written in ", lines[^1]);

        // The timeline: a step starts when its first file arrives, so write starts once markdown has processed a file (20 ms
        // in) and its bar starts further right.
        Assert.True(lines[header + 3].IndexOf('█') > lines[header + 2].IndexOf('█') || !lines[header + 3].Contains('█'));
    }

    [Fact]
    public async Task ThrottlingKeepsTheFinalState()
    {
        var files = Enumerable.Range(0, 300).Select(x => $"/{x:000}.md").ToArray();
        var site = await new FakeSite().WithFilesAsync(files);
        var console = Console(live: true);

        var result = await site.BuildAsync(new FakeWorkflow()
            .Add("markdown", d => new FakeMapStep(d), inputs: ["**"])
            .Add("layout", d => new FakeMapStep(d) { Delay = TimeSpan.FromMilliseconds(1) }, needs: ["markdown"])
            .Write("write", "layout")
            .Build(), view: new SpectreProgressView(console));

        Assert.True(result.Succeeded);

        var lines = Lines(console);

        // Every completion line printed once, in completion order, then the summary with the final counters.
        Assert.Single(lines, x => x.StartsWith("✓ markdown ", StringComparison.Ordinal));
        Assert.Single(lines, x => x.StartsWith("✓ layout ", StringComparison.Ordinal));
        Assert.Single(lines, x => x.StartsWith("✓ write ", StringComparison.Ordinal));
        Assert.Contains(lines, x => Regex.IsMatch(x, @"^✓ write\s+300 files"));
        Assert.Contains(lines, x => Regex.IsMatch(x, @"layout\s+succeeded\s+300\s+300\s+300\s+0\s"));
        Assert.StartsWith("Build succeeded: 3 steps, 300 files written in ", lines[^1]);

        // The live display drew the running steps at least once.
        Assert.Contains("steps done", console.Output);
    }
}
