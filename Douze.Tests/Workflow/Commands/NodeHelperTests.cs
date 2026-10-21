using Douze.Cli;
using Douze.FileSystems;
using Douze.Nodes;
using Douze.Progress;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow.Commands;

/// <summary>
/// Runs workers written with the real <c>douze-worker.mjs</c>. Skipped when <c>node</c> is not on the PATH.
/// </summary>
public class NodeHelperTests : CommandTestBase
{
    private const string WorkerScript = """
        import { serve } from './douze-worker.mjs';

        serve(async (file, ctx) => {
          if (Buffer.isBuffer(file.content)) {
            return { content: Buffer.from(file.content.map((b) => 255 - b)), metadata: { binary: true } };
          }
          if (file.content.includes('FAIL')) {
            const error = new Error(`cannot handle ${file.path}`);
            error.line = 2;
            error.column = 5;
            throw error;
          }
          ctx.log(`handled ${file.path}`);
          return { content: file.content.toUpperCase() + ctx.options.suffix, metadata: { step: ctx.step, title: file.metadata.title } };
        }, { concurrency: 2, name: 'node-test' });
        """;

    private static readonly string? Node = FindNode();

    private async Task<BuildResult> RunWorkerAsync()
    {
        if (Node == null)
        {
            Assert.Skip("node is not on the PATH");
        }

        await using (var helper = File.Create(Path.Combine(TempFolder, "douze-worker.mjs")))
        {
            WorkerHelperCommand.Write(helper);
        }

        var script = Path.Combine(TempFolder, "worker.mjs");
        await File.WriteAllTextAsync(script, WorkerScript, TestContext.Current.CancellationToken);

        return await RunAsync(Workflow(
            Step("pages", "frontmatter", inputs: ["**"]),
            Command("node", $"node \"{script}\"", CommandMode.Worker, needs: ["pages"], with: With(("suffix", NodeValue.Create("!"))))),
            failure: FailurePolicy.KeepGoing);
    }

    [Fact]
    public async Task EchoThroughTheHelper()
    {
        await Source.WriteTextFileAsync("/docs/a.md", "---\ntitle: A\n---\nhello é");
        await Source.WriteTextFileAsync("/docs/b.md", "world");

        var result = await RunWorkerAsync();
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));

        var run = result["node"];
        Assert.Equal("HELLO É!", await OutputTextAsync(run, "/docs/a.md"));
        Assert.Equal("WORLD!", await OutputTextAsync(run, "/docs/b.md"));

        var a = Outputs(run).Single(x => x.Path == "/docs/a.md");
        Assert.Equal("node", a.Metadata.Own["step"]!.GetValue<string>());
        Assert.Equal("A", a.Metadata.Own["title"]!.GetValue<string>());

        Assert.Contains(Messages, x => x is { Step: "node", Message: "handled /docs/a.md" });
        Assert.Contains(Messages, x => x is { Step: "node", Message: "worker 'node-test' ready, 2 request(s) at once" });
    }

    [Fact]
    public async Task BinaryRoundTrip()
    {
        byte[] bytes = [0, 1, 2, 128, 255];
        await Source.WriteBinaryFileAsync("/image.png", bytes);

        var result = await RunWorkerAsync();
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));

        var image = Outputs(result["node"]).Single();
        Assert.False(image.Content.IsText);
        Assert.Equal(bytes.Select(x => (byte)(255 - x)), (await image.Content.ReadBytesAsync(TestContext.Current.CancellationToken)).ToArray());
        Assert.True(image.Metadata.Own["binary"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ErrorPath()
    {
        await Source.WriteTextFileAsync("/a.md", "FAIL");
        await Source.WriteTextFileAsync("/b.md", "fine");

        var result = await RunWorkerAsync();

        var error = Assert.Single(result.Errors);
        Assert.Equal("/a.md", error.File?.FullName);
        Assert.Equal("cannot handle /a.md (line 2, column 5)", error.Message);
        Assert.Equal("FINE!", await OutputTextAsync(result["node"], "/b.md"));

        // The stack trace is a debug message.
        Assert.Contains(Messages, x => x.Level == LogLevel.Debug && x.Message.StartsWith("/a.md: Error: cannot handle /a.md", StringComparison.Ordinal));
    }

    [Fact]
    public void WorkerHelperPrintsTheHelper()
    {
        using var stream = new MemoryStream();
        WorkerHelperCommand.Write(stream);

        var helper = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        Assert.StartsWith("// douze-worker.mjs: helper for Douze worker steps (protocol 1).", helper);
        Assert.Contains("export function serve(handler", helper);
    }

    private static string? FindNode()
    {
        var names = OperatingSystem.IsWindows() ? (string[])["node.exe"] : ["node"];

        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var path = Path.Combine(folder, name);

                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }
}
