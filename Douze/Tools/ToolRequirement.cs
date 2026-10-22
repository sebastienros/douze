using Douze.Workflow;

namespace Douze.Tools;

/// <summary>
/// A tool that Douze installs into the tools folder for a step (<see cref="ToolInstaller"/>): found when it is
/// installed, installable when a build can install it on first use (<c>douze tools install</c> does it now), missing
/// otherwise. Its hint says how to fix a missing one, such as the option that gives the path of a binary.
/// </summary>
internal abstract class ToolRequirement(string name, string hint) : Requirement(name, hint)
{
    /// <summary>
    /// The folder the tool is installed in: two requirements with the same folder are the same install.
    /// </summary>
    public abstract string Folder { get; }

    /// <summary>
    /// Installs the tool when it is not, and returns the path of its binary.
    /// </summary>
    /// <exception cref="DouzeException">The tool cannot be installed on this machine, or the install failed.</exception>
    public abstract Task<string> InstallAsync(CancellationToken ct);
}
