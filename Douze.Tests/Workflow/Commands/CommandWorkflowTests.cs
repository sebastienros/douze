using Douze.Cli;
using Douze.Workflow;
using Douze.Workflow.Yaml;
using Xunit;

namespace Douze.Tests.Workflow.Commands;

public class CommandWorkflowTests : CommandTestBase
{
    /// <summary>
    /// The site of <c>Fixtures/commands</c> through its <c>douze.yml</c>: a filter, a worker and a batch step, built from
    /// and to disk.
    /// </summary>
    [Fact]
    public async Task AllThreeModes()
    {
        var (source, output) = CopyFixture();
        var (workflow, site) = await WorkflowSource.LoadAsync(CommandLine.Parse(["-s", source, "-o", output]), source);
        Site = site;

        var result = await RunSuccessfullyAsync(workflow);

        Assert.Equal(
            ["/docs/guide.md", "/docs/intro.md", "/notes/a.txt", "/notes/b.txt", "/titles.txt"],
            result.Outputs.Select(x => x.Path.FullName));

        Assert.Equal("ALPHA", await File.ReadAllTextAsync(Path.Combine(output, "notes", "a.txt"), TestContext.Current.CancellationToken));
        Assert.Equal("INTRO PAGE", await File.ReadAllTextAsync(Path.Combine(output, "docs", "intro.md"), TestContext.Current.CancellationToken));
        Assert.Equal(
            "/docs/guide.md:text/markdown:Guide\n/docs/intro.md:text/markdown:Intro",
            await File.ReadAllTextAsync(Path.Combine(output, "titles.txt"), TestContext.Current.CancellationToken));

        // The temporary folders of the batch step are gone.
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(TempFolder, "cache", "tmp")));
    }

    [Fact]
    public async Task PrintedWorkflowLoadsBackTheSame()
    {
        var (source, _) = CopyFixture();
        var (workflow, _) = await WorkflowSource.LoadAsync(CommandLine.Parse(["-s", source]), source);

        var yaml = WorkflowGraph.Build(workflow).ToYaml();

        Assert.Equal("""
            steps:
              pages: { inputs: ["notes/**", "docs/**"], uses: frontmatter }
              upper: { needs: pages, match: "**/*.txt", run: dotnet tools/Douze.Tests.FakeTool.dll filter-upper, mode: filter }
              work:  { needs: upper, match: "**/*.md", run: dotnet tools/Douze.Tests.FakeTool.dll worker-echo 2, mode: worker }
              index: { needs: work, match: "**/*.md", run: "dotnet tools/Douze.Tests.FakeTool.dll batch-manifest {{ inputs }} {{ outputs }}" }
              write: { needs: [work, index], uses: write }

            """.ReplaceLineEndings("\n"), yaml.ReplaceLineEndings("\n"));

        var printed = WorkflowLoader.Load(yaml, "printed.yml").Workflow!;
        Assert.Equal(workflow.Steps.Select(x => (x.Name, x.ConfigHash)), printed.Steps.Select(x => (x.Name, x.ConfigHash)));
    }

    /// <summary>
    /// Copies the fixture site and the fake tool (its dll and runtime configuration) into <c>tools/</c>.
    /// </summary>
    private (string Source, string Output) CopyFixture()
    {
        var source = Path.Combine(TempFolder, "site");
        var output = Path.Combine(TempFolder, "out");
        CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "commands"), source);
        Directory.CreateDirectory(output);

        var tools = Path.Combine(source, "tools");
        Directory.CreateDirectory(tools);

        foreach (var file in new[] { FakeToolPath, Path.ChangeExtension(FakeToolPath, ".runtimeconfig.json") })
        {
            File.Copy(file, Path.Combine(tools, Path.GetFileName(file)));
        }

        return (source, output);
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
