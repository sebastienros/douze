using Douze.Workflow;

namespace Douze.Tools;

/// <summary>
/// A tool published on npm that a step runs (<see cref="NpmTool"/>): found when it is installed in the tools folder,
/// installable when it is not but has a package for the platform (a build downloads it on first use, <c>douze tools
/// install</c> now), missing otherwise. Its hint says how to fix a platform without package, such as the option that
/// gives the path of a binary.
/// </summary>
internal sealed class NpmToolRequirement(NpmTool tool, string version, ToolInstaller installer, string hint, string? platform = null)
    : Requirement($"{tool.Name} {version}", hint)
{
    private readonly string _platform = platform ?? NpmTool.CurrentPlatform;

    /// <summary>
    /// The folder the tool is installed in: two requirements with the same folder are the same install.
    /// </summary>
    public string Folder => installer.GetFolder(tool, version, _platform);

    public override ValueTask<RequirementStatus> CheckAsync(CancellationToken ct)
    {
        if (installer.FindInstalled(tool, version, _platform) is { } executable)
        {
            return ValueTask.FromResult(RequirementStatus.Found(version, executable));
        }

        return ValueTask.FromResult(tool.Packages.ContainsKey(_platform)
            ? RequirementStatus.Installable($"is not installed: it is downloaded for {_platform} on first use, or now by 'douze tools install'")
            : RequirementStatus.Missing($"has no package for the platform '{_platform}' (it has {string.Join(", ", tool.Packages.Keys.Order(StringComparer.Ordinal))})"));
    }

    /// <summary>
    /// Installs the tool when it is not, and returns the path of its binary.
    /// </summary>
    /// <exception cref="DouzeException">The tool has no package for the platform, or the download failed.</exception>
    public Task<string> InstallAsync(CancellationToken ct) => installer.InstallAsync(tool, version, ct, _platform);
}
