using Douze.Workflow;

namespace Douze.Tools;

/// <summary>
/// A tool published on NuGet that a step runs (<see cref="NuGetTool"/>): installable when it has a package for the
/// platform.
/// </summary>
internal sealed class NuGetToolRequirement(NuGetTool tool, string version, ToolInstaller installer, string hint, string? platform = null)
    : ToolRequirement($"{tool.Name} {version}", hint)
{
    private readonly string _platform = platform ?? NpmTool.CurrentPlatform;

    public override string Folder => installer.GetFolder(tool, version, _platform);

    public override ValueTask<RequirementStatus> CheckAsync(CancellationToken ct)
    {
        if (installer.FindInstalled(tool, version, _platform) is { } path)
        {
            return ValueTask.FromResult(RequirementStatus.Found(version, path));
        }

        return ValueTask.FromResult(tool.Packages.ContainsKey(_platform)
            ? RequirementStatus.Installable($"is not installed: it is downloaded for {_platform} on first use, or now by 'douze tools install'")
            : RequirementStatus.Missing($"has no package for the platform '{_platform}' (it has {string.Join(", ", tool.Packages.Keys.Order(StringComparer.Ordinal))})"));
    }

    public override Task<string> InstallAsync(CancellationToken ct) => installer.InstallAsync(tool, version, ct, _platform);
}
