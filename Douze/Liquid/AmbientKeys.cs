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

    /// <summary>The <see cref="Workflow.LocaleSet"/> of the site (<c>site.locales</c>).</summary>
    public const string Locales = "douze:locales";

    /// <summary>The <see cref="Workflow.StepContext"/> of the unit being rendered, for filters that report messages.</summary>
    public const string StepContext = "douze:step-context";
}
