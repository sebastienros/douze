using Douze.Workflow;

namespace Douze.Tools;

/// <summary>
/// A tool that a step builds from Go source (<see cref="GoTool"/>): installable when the <c>go</c> executable
/// <paramref name="go"/> exists (null when it was not found), which builds it.
/// </summary>
internal sealed class GoToolRequirement(GoTool tool, string version, ToolInstaller installer, string? go, string hint)
    : ToolRequirement($"{tool.Name} {version}", hint)
{
    public override string Folder => installer.GetFolder(tool, version);

    public override ValueTask<RequirementStatus> CheckAsync(CancellationToken ct)
    {
        if (installer.FindInstalled(tool, version) is { } executable)
        {
            return ValueTask.FromResult(RequirementStatus.Found(version, executable));
        }

        return ValueTask.FromResult(go != null
            ? RequirementStatus.Installable("is not installed: it is built with go install on first use, or now by 'douze tools install'")
            : RequirementStatus.Missing("is not installed, and Go is not found to build it"));
    }

    public override Task<string> InstallAsync(CancellationToken ct) => go != null
        ? installer.InstallAsync(tool, version, go, ct)
        : throw new DouzeException($"cannot build {tool.Name}: Go is not found");
}
