using Douze.Progress;
using Douze.Tests.Workflow;
using Douze.Workflow;
using Douze.Workflow.Scheduling;
using Xunit;

namespace Douze.Tests.Progress;

public class PlainProgressViewTests
{
    private static async Task<(BuildResult Result, string[] Lines)> BuildAsync(FakeWorkflow workflow, bool unicode, params string[] files)
    {
        var site = await new FakeSite().WithFilesAsync(files);
        var output = new StringWriter();
        var result = await site.BuildAsync(workflow.Build(), view: new PlainProgressView(output, unicode: unicode));

        return (result, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task StepLineFormat()
    {
        var (result, lines) = await BuildAsync(new FakeWorkflow()
            .Add("markdown", d => new FakeMapStep(d), inputs: ["**"])
            .Write("write", "markdown"), unicode: true, "/a.md", "/b.md");

        var markdown = result["markdown"];
        var seconds = ((markdown.EndedAt!.Value - markdown.StartedAt!.Value).TotalSeconds).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal($"✓ markdown         2 files {seconds,7} s", lines[0]);
        Assert.StartsWith("✓ write            2 files ", lines[1]);
        Assert.StartsWith("Build succeeded: 2 steps, 2 files written in ", lines[2]);
    }

    [Fact]
    public async Task OneFile()
    {
        var (_, lines) = await BuildAsync(new FakeWorkflow()
            .Add("markdown", d => new FakeMapStep(d), inputs: ["**"])
            .Write("write", "markdown"), unicode: true, "/a.md");

        // Singular, with the seconds still aligned with "   312 files".
        Assert.Matches(@"^✓ markdown         1 file     0\.\d\d s$", lines[0]);
        Assert.StartsWith("Build succeeded: 2 steps, 1 file written in ", lines[2]);
    }

    [Fact]
    public async Task SkippedLine()
    {
        var (_, lines) = await BuildAsync(new FakeWorkflow()
            .Add("broken", d => new FakeMapStep(d) { FailWhen = _ => true }, inputs: ["**"])
            .Add("og-images", d => new FakeMapStep(d), needs: ["broken"]), unicode: false, "/a.md");

        Assert.Contains("FAIL broken      error: broken: /a.md: failed on /a.md", lines);
        Assert.Contains("skip og-images   skipped", lines);
    }

    [Fact]
    public async Task SummaryWithErrors()
    {
        var site = await new FakeSite().WithFilesAsync("/a.md", "/b.md");
        var output = new StringWriter();

        await site.BuildAsync(new FakeWorkflow()
            .Add("render", d => new FakeMapStep(d) { FailWhen = _ => true }, inputs: ["**"])
            .Build(), FailurePolicy.KeepGoing, view: new PlainProgressView(output, unicode: true));

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("warning: no 'write' step: nothing will be written to the output folder", lines[0]);
        Assert.Equal("✗ render      error: render: /a.md: failed on /a.md (and 1 more error)", lines[1]);
        Assert.StartsWith("Build failed with 2 errors in ", lines[2]);
        Assert.Equal("error: render: /a.md: failed on /a.md", lines[3]);
        Assert.Equal("error: render: /b.md: failed on /b.md", lines[4]);
        Assert.Equal(5, lines.Length);
    }
}
