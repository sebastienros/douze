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

    private readonly Lock _addedLock = new();
    private List<VirtualFile>? _added;

    /// <summary>
    /// Adds a file to the outputs of the current unit, besides what the step returns: a tag that produces a file (an
    /// image variant) calls it through the ambient values (<see cref="AmbientKeys.StepContext"/>). Outputs are stored
    /// with the unit's result and replayed with it; their provenance is the unit's. Two units may add the same file
    /// (same path, content and metadata): <c>write</c> accepts identical files for one path and writes it once. A file
    /// added twice by one unit is kept once.
    /// </summary>
    public void AddOutput(VirtualFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        lock (_addedLock)
        {
            _added ??= [];

            foreach (var added in _added)
            {
                if (added.Path == file.Path && added.Ref == file.Ref)
                {
                    return;
                }
            }

            _added.Add(file);
        }
    }

    /// <summary>
    /// The files added so far with <see cref="AddOutput"/>, sorted by path; read by the scheduler when the unit ends.
    /// </summary>
    public IReadOnlyList<VirtualFile> AddedOutputs
    {
        get
        {
            lock (_addedLock)
            {
                return _added == null ? [] : [.. _added.OrderBy(x => x.Path)];
            }
        }
    }

    /// <summary>
    /// A template context whose model is <paramref name="model"/>, with the ambient values every render needs: the
    /// scopes, the dependency tracker of the unit, the file being rendered, the site's locales, this context and the
    /// model (for tags that render with the page's names).
    /// </summary>
    public TemplateContext CreateTemplateContext(FluidValue model, VirtualFile? file, CancellationToken ct)
    {
        var context = new TemplateContext(model, Build.LiquidOptions) { CancellationToken = ct };
        context.AmbientValues[AmbientKeys.Scopes] = Scopes;
        context.AmbientValues[AmbientKeys.Dependencies] = Dependencies;
        context.AmbientValues[AmbientKeys.Locales] = Build.Locales;
        context.AmbientValues[AmbientKeys.StepContext] = this;
        context.AmbientValues[AmbientKeys.Model] = model;

        if (file != null)
        {
            context.AmbientValues[AmbientKeys.File] = file;
        }

        return context;
    }
}
