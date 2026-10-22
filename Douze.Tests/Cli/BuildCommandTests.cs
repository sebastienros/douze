using System.Text.Json;
using Douze.Cli;
using Xunit;

namespace Douze.Tests.Cli;

public class BuildCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("douze-build-").FullName;

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task WritesTheTraceAndTheTimeline()
    {
        var source = Path.Combine(_root, "site");
        Directory.CreateDirectory(Path.Combine(source, "_content"));
        await File.WriteAllTextAsync(Path.Combine(source, "_content", "index.md"), "# Hello", TestContext.Current.CancellationToken);

        var trace = Path.Combine(_root, "traces", "out.json");
        var timeline = Path.Combine(_root, "traces", "out.html");

        var exitCode = await CommandLine.RunAsync(["-s", source, "-o", Path.Combine(_root, "out"), "--quiet", "--trace", trace, "--timeline", timeline]);

        Assert.Equal(ExitCodes.Success, exitCode);

        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(trace, TestContext.Current.CancellationToken));
        var names = json.RootElement.GetProperty("traceEvents").EnumerateArray()
            .Where(x => x.GetProperty("ph").GetString() == "X")
            .Select(x => x.GetProperty("name").GetString())
            .ToArray();

        Assert.Contains("build", names);
        Assert.Contains("markdown", names);
        Assert.Equal(source, json.RootElement.GetProperty("otherData").GetProperty("site").GetString());

        var html = await File.ReadAllTextAsync(timeline, TestContext.Current.CancellationToken);
        Assert.Contains("\"name\":\"markdown\"", html);
    }

    [Fact]
    public async Task ErrorTagFailsTheBuild()
    {
        var source = Path.Combine(_root, "site");
        Directory.CreateDirectory(Path.Combine(source, "_content"));
        await File.WriteAllTextAsync(Path.Combine(source, "_content", "index.md"), "# Hello\n{% error \"unknown reference\" %}", TestContext.Current.CancellationToken);

        var exitCode = await CommandLine.RunAsync(["-s", source, "-o", Path.Combine(_root, "out"), "--quiet", "--no-cache"]);

        Assert.Equal(ExitCodes.BuildErrors, exitCode);
    }

    [Fact]
    public async Task InvalidShortcodeStopsBeforeTheBuild()
    {
        var source = Path.Combine(_root, "site");
        Directory.CreateDirectory(Path.Combine(source, "_content"));
        Directory.CreateDirectory(Path.Combine(source, "_includes", "shortcodes"));
        await File.WriteAllTextAsync(Path.Combine(source, "_content", "index.md"), "# Hello", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(source, "_includes", "shortcodes", "raw.liquid"), "x", TestContext.Current.CancellationToken);

        var exitCode = await CommandLine.RunAsync(["-s", source, "-o", Path.Combine(_root, "out"), "--quiet", "--no-cache"]);

        Assert.Equal(ExitCodes.Usage, exitCode);
    }
}
