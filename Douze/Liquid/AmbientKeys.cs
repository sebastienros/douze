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

    /// <summary>The <see cref="Workflow.StepContext"/> of the unit: the build's services, logging.</summary>
    public const string StepContext = "douze:step";

    /// <summary>
    /// The model of the unit's page (a <see cref="MetadataValue"/>), for tags that render with the page's names
    /// (shortcodes).
    /// </summary>
    public const string Model = "douze:model";

    /// <summary>
    /// <c>true</c> while the output of the render is Markdown source (the body of a Markdown page, of a shortcode in
    /// it, of a Markdown include): block tags write raw regions and bodies are rendered to HTML.
    /// </summary>
    public const string Markdown = "douze:markdown";

    /// <summary>The <see cref="FenceTable"/> of the Markdown being rendered: its protected code fences.</summary>
    public const string Fences = "douze:fences";

    /// <summary><c>true</c> when an undefined variable is an error (<c>with.strict</c> of the <c>liquid</c> step).</summary>
    public const string Strict = "douze:strict";

    /// <summary>The path of the template being rendered, which positions reported by tags are in.</summary>
    public const string TemplatePath = "douze:template";

    /// <summary>The chain of Markdown includes being rendered (a list of paths), for the recursion limit.</summary>
    public const string Includes = "douze:includes";
}
