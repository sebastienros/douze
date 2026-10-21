namespace Douze.Liquid;

/// <summary>
/// Keys of <see cref="Fluid.TemplateContext.AmbientValues"/> set for every render.
/// </summary>
internal static class AmbientKeys
{
    /// <summary>The <see cref="Workflow.ScopeSet"/> metadata is resolved against.</summary>
    public const string Scopes = "douze:scopes";

    /// <summary>The <see cref="Workflow.DependencyTracker"/> of the unit being rendered.</summary>
    public const string Dependencies = "douze:dependencies";

    /// <summary>The <see cref="Workflow.VirtualFile"/> being rendered.</summary>
    public const string File = "douze:file";
}
