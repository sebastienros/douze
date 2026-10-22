using Douze.Liquid;
using Douze.Progress;
using Douze.Tracing;
using Fluid;
using Fluid.Values;

namespace Douze.Workflow;

/// <summary>
/// What a step receives with each unit of work.
/// </summary>
internal sealed class StepContext
{
    public StepContext(StepDefinition definition, ScopeSet scopes, BuildContext build, DependencyTracker dependencies, TraceSpan span)
    {
        Definition = definition;
        Scopes = scopes;
        Build = build;
        Dependencies = dependencies;
        Span = span;
    }

    public StepDefinition Definition { get; }

    public string StepName => Definition.Name;

    /// <summary>
    /// The contributions of every ancestor step, merged. Read them through <see cref="ScopeSet.TryResolve"/> only.
    /// </summary>
    public ScopeSet Scopes { get; }

    public BuildContext Build { get; }

    /// <summary>
    /// The tracker of the current unit of work.
    /// </summary>
    public DependencyTracker Dependencies { get; }

    /// <summary>
    /// The span of the current unit, so steps can add arguments to the trace.
    /// </summary>
    public TraceSpan Span { get; }

    public void Log(LogLevel level, string message) => Build.View.Message(level, StepName, message);

    /// <summary>
    /// A template context whose model is <paramref name="model"/>, with the ambient values every render needs: the
    /// scopes, the dependency tracker of the unit, the file being rendered, the site's locales and this context.
    /// </summary>
    public TemplateContext CreateTemplateContext(FluidValue model, VirtualFile? file, CancellationToken ct)
    {
        var context = new TemplateContext(model, Build.LiquidOptions) { CancellationToken = ct };
        context.AmbientValues[AmbientKeys.Scopes] = Scopes;
        context.AmbientValues[AmbientKeys.Dependencies] = Dependencies;
        context.AmbientValues[AmbientKeys.Locales] = Build.Locales;
        context.AmbientValues[AmbientKeys.StepContext] = this;

        if (file != null)
        {
            context.AmbientValues[AmbientKeys.File] = file;
        }

        return context;
    }
}
