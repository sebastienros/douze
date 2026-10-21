using Douze.FileSystems;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow.Commands;

public class CommandWorkflowTests : CommandTestBase
{
    /// <summary>
    /// The site of <c>Fixtures/commands</c> through a filter, a worker and a batch step, built from and to disk.
    /// </summary>
    [Fact]
    public async Task AllThreeModes()
    {
        var source = Path.Combine(TempFolder, "site");
        var output = Path.Combine(TempFolder, "out");
        CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "commands"), source);
        Directory.CreateDirectory(output);

        Site = await SiteOptions.DetectAsync(new PhysicalFileSystem(source), new PhysicalFileSystem(output), source, output);

        var result = await RunSuccessfullyAsync(Workflow(
            Step("pages", "frontmatter", inputs: ["notes/**", "docs/**"]),
            Command("upper", Tool("filter-upper"), CommandMode.Filter, needs: ["pages"], match: ["**/*.txt"]),
            Command("work", Tool("worker-echo", "2"), CommandMode.Worker, needs: ["upper"], match: ["**/*.md"]),
            Command("index", Tool("batch-manifest", "{{ inputs }}", "{{ outputs }}"), CommandMode.Batch, needs: ["work"], match: ["**/*.md"]),
            Step("write", "write", needs: ["work", "index"])));

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
