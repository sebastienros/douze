namespace Douze.Tools;

/// <summary>
/// A tool published as Go source only (such as gopls, which has no prebuilt binaries): Douze builds it with
/// <c>go install &lt;module&gt;@&lt;version&gt;</c>, which needs a Go toolchain, into the tools folder.
/// </summary>
/// <param name="Name">The tool's name, its folder under the tools folder.</param>
/// <param name="Module">The path of its main package, as <c>go install</c> takes it.</param>
/// <param name="DefaultVersion">The version used when none is configured.</param>
/// <param name="Executable">The name of the binary that <c>go install</c> writes (without <c>.exe</c>).</param>
internal sealed record GoTool(string Name, string Module, string DefaultVersion, string Executable)
{
    /// <summary>
    /// gopls, the Go language server. Version 0.24 needs Go 1.27 to build, which <c>go install</c> fetches by itself
    /// (<c>GOTOOLCHAIN=auto</c>, the default).
    /// </summary>
    public static GoTool Gopls { get; } = new("gopls", "golang.org/x/tools/gopls", "v0.24.0", "gopls");
}
