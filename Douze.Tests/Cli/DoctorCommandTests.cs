using Douze.Cli;
using Douze.Tests.Tools;
using Douze.Tools;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Cli;

public sealed class DoctorCommandTests : IDisposable
{
    private const string MissingTool = "douze-missing-tool-4ae1";

    private readonly string _root = Directory.CreateTempSubdirectory("douze-doctor-").FullName;
    private readonly FakeRegistry _registry = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Source => Path.Combine(_root, "site");

    private string Cache => Path.Combine(_root, "cache");

    public void Dispose()
    {
        _registry.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private async Task WriteWorkflowAsync(string steps)
    {
        Directory.CreateDirectory(Source);
        await File.WriteAllTextAsync(Path.Combine(Source, WorkflowSource.DefaultFileName), "steps:\n" + steps, Ct);
    }

    private async Task<(int ExitCode, string Output)> DoctorAsync()
    {
        using var output = new StringWriter();
        var exitCode = await DoctorCommand.RunAsync(CommandLine.Parse(["doctor", "-s", Source, "--cache-dir", Cache]), output);
        return (exitCode, output.ToString());
    }

    private async Task<(int ExitCode, string Output, string Error)> InstallAsync()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await ToolsCommand.InstallAsync(CommandLine.Parse(["tools", "install", "-s", Source, "--cache-dir", Cache]), output, error);
        return (exitCode, output.ToString(), error.ToString());
    }

    [Fact]
    public void ParsesTheCommands()
    {
        Assert.Equal(Command.Doctor, CommandLine.Parse(["doctor", "-s", "site"]).Command);
        Assert.Equal(Command.ToolsInstall, CommandLine.Parse(["tools", "install", "-s", "site"]).Command);
        Assert.Equal("'tools' needs a subcommand: 'tools install'", Assert.Throws<CommandLineException>(() => CommandLine.Parse(["tools"])).Message);
        Assert.Equal("'tools' needs a subcommand: 'tools install'", Assert.Throws<CommandLineException>(() => CommandLine.Parse(["tools", "-s", "site"])).Message);
        Assert.Equal("unknown tools subcommand 'list'", Assert.Throws<CommandLineException>(() => CommandLine.Parse(["tools", "list"])).Message);
    }

    [Fact]
    public void FormatsATableWithTheHintsOfTheMissingRequirements()
    {
        var found = Requirement.Create("node", "", _ => default);
        var missing = Requirement.Create(MissingTool, "install it", _ => default);
        var installable = Requirement.Create("typescript 7.0.2", "set 'path'", _ => default);

        var text = DoctorCommand.Format(
        [
            new("images", found, RequirementStatus.Found("8.18.7")),
            new("worker", found, RequirementStatus.Found(path: "/usr/bin/node")),
            new("check", installable, RequirementStatus.Installable("is not installed")),
            new("tool", missing, RequirementStatus.Missing("is not on PATH")),
        ]);

        Assert.Equal($"""
            STEP    REQUIREMENT              STATUS    DETAILS
            images  node                     ok        8.18.7
            worker  node                     ok        /usr/bin/node
            check   typescript 7.0.2         download  is not installed
            tool    {MissingTool}  missing   is not on PATH: install it

            1 requirement missing: builds fail until it is fixed

            """.ReplaceLineEndings("\n"), text);

        Assert.Equal(
            "Every requirement is met: 1 tool will be downloaded on first use ('douze tools install' downloads it now)\n",
            DoctorCommand.Format([new("check", installable, RequirementStatus.Installable("is not installed"))]).Split('\n', 4)[3]);
        Assert.Equal("No step of the workflow has requirements.\n", DoctorCommand.Format([]).ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task ChecksTheRequirementsOfTheWorkflowWithoutBuilding()
    {
        var tsc = Path.Combine("bin", "tsc");
        await WriteWorkflowAsync($$"""
              echo: { inputs: ["**/*.txt"], run: echo hi, mode: filter }
              tool: { inputs: ["**/*.txt"], run: {{MissingTool}} --flag, mode: filter }
              check: { inputs: ["**/*.html"], uses: hover-typescript, with: { path: {{tsc}} } }
              write: { needs: [echo, tool, check], uses: write }

            """);

        var (exitCode, output) = await DoctorAsync();

        Assert.Equal(ExitCodes.BuildErrors, exitCode);
        Assert.Contains("echo   echo", output);
        Assert.Contains($"tool   {MissingTool}", output);
        Assert.Contains($"missing  is not on PATH: install {MissingTool} and add its folder to PATH, or change the 'run' line of step 'tool'", output);
        Assert.Contains($"check  typescript", output);
        Assert.Contains($"does not exist at '{Path.Combine(Source, tsc)}': set the option 'path' of step 'check' to a typescript binary, or remove it to download typescript", output);
        Assert.EndsWith("2 requirements missing: builds fail until they are fixed\n", output.ReplaceLineEndings("\n"));
        Assert.False(Directory.Exists(Path.Combine(Source, "_site")));
        Assert.False(Directory.Exists(Path.Combine(_root, "_site")));

        // The build fails before running the commands, with the same hints.
        var build = await CommandLine.RunAsync(["-s", Source, "-o", Path.Combine(_root, "out"), "--quiet", "--cache-dir", Cache]);
        Assert.Equal(ExitCodes.BuildErrors, build);
    }

    [Fact]
    public async Task ChecksTheRequirementsOfTheCodeHoverSteps()
    {
        var dotnet = Path.Combine("missing", "dotnet");
        var go = Path.Combine("missing", "go");
        await WriteWorkflowAsync($$"""
              cs: { inputs: ["**/*.html"], uses: hover-csharp, with: { dotnet: {{dotnet}}, version: 0.0.1-test } }
              golang: { needs: cs, uses: hover-go, with: { go: {{go}}, version: v0.0.1 } }

            """);

        var (exitCode, output) = await DoctorAsync();

        Assert.Equal(ExitCodes.BuildErrors, exitCode);
        Assert.Contains($"does not exist at '{Path.Combine(Source, dotnet)}': install the .NET SDK (10 or later) from https://dot.net, or set the option 'dotnet' of step 'cs'", output);
        Assert.Contains($"roslyn 0.0.1-test", output);
        Assert.Contains($"is not installed: it is downloaded for {NpmTool.CurrentPlatform} on first use", output);
        Assert.Contains($"does not exist at '{Path.Combine(Source, go)}': install Go from https://go.dev/dl, or set the option 'go' of step 'golang'", output);
        Assert.Contains("gopls v0.0.1", output);
        Assert.Contains("is not installed, and Go is not found to build it: install Go from https://go.dev/dl to build gopls, or set the option 'path' of step 'golang' to a gopls binary", output);
        Assert.EndsWith("3 requirements missing: builds fail until they are fixed\n", output.ReplaceLineEndings("\n"));
        Assert.Empty(_registry.Requests);
    }

    [Fact]
    public async Task AWorkflowWithoutRequirementsIsHealthy()
    {
        Directory.CreateDirectory(Path.Combine(Source, "_content"));

        var (exitCode, output) = await DoctorAsync();

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("No step of the workflow has requirements.\n", output.ReplaceLineEndings("\n"));

        var install = await InstallAsync();
        Assert.Equal((ExitCodes.Success, "No step of the workflow downloads tools.\n", ""), (install.ExitCode, install.Output.ReplaceLineEndings("\n"), install.Error));
    }

    [Fact]
    public async Task ToolsInstallDownloadsTheToolsOfTheWorkflowOnce()
    {
        const string version = "0.0.1-test";
        var platform = NpmTool.CurrentPlatform;
        Assert.SkipUnless(NpmTool.TypeScript.Packages.TryGetValue(platform, out var package), $"TypeScript has no package for {platform}");

        var executable = NpmTool.TypeScript.Executable + (OperatingSystem.IsWindows() ? ".exe" : "");
        _registry.Publish(package, version, FakeRegistry.Tarball((executable, "#!/bin/sh\n")));

        // Two steps with the same engine install it once.
        await WriteWorkflowAsync($$"""
              check: { inputs: ["**/*.html"], uses: hover-typescript, with: { version: {{version}}, registry: "{{_registry.Url}}" } }
              again: { needs: check, uses: hover-typescript, with: { version: {{version}}, registry: "{{_registry.Url}}" } }

            """);

        var before = await DoctorAsync();
        Assert.Equal(ExitCodes.Success, before.ExitCode);
        Assert.Contains($"check  typescript {version}  download  is not installed: it is downloaded for {platform} on first use, or now by 'douze tools install'", before.Output);
        Assert.Contains("Every requirement is met: 2 tools will be downloaded on first use", before.Output);
        Assert.Empty(_registry.Requests);

        var path = Path.Combine(Cache, "tools", "typescript", version, platform, executable);
        var install = await InstallAsync();

        Assert.Equal(ExitCodes.Success, install.ExitCode);
        Assert.Equal("", install.Error);
        Assert.Contains($"check: downloading typescript {version} ({package})", install.Output);
        Assert.EndsWith($"typescript {version}: installed at {path}\n", install.Output.ReplaceLineEndings("\n"));
        Assert.True(File.Exists(path));
        Assert.Equal(2, _registry.Requests.Count);

        var after = await DoctorAsync();
        Assert.Equal(ExitCodes.Success, after.ExitCode);
        Assert.Contains($"check  typescript {version}  ok      {version} at {path}", after.Output);
        Assert.EndsWith("Every requirement is met\n", after.Output.ReplaceLineEndings("\n"));

        var again = await InstallAsync();
        Assert.Equal($"typescript {version}: already installed at {path}\n", again.Output.ReplaceLineEndings("\n"));
        Assert.Equal(2, _registry.Requests.Count);
    }

    [Fact]
    public async Task ToolsInstallReportsAToolThatCannotBeDownloaded()
    {
        await WriteWorkflowAsync($$"""
              check: { inputs: ["**/*.html"], uses: hover-typescript, with: { version: 0.0.2-absent, registry: "{{_registry.Url}}" } }

            """);

        var install = await InstallAsync();

        Assert.Equal(ExitCodes.BuildErrors, install.ExitCode);
        Assert.StartsWith("error: check: cannot install typescript 0.0.2-absent: cannot find @typescript/", install.Error);
    }
}
