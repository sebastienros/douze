namespace Douze.Workflow;

/// <summary>
/// A step implementation. It declares which shapes it supports and implements the matching interface:
/// <see cref="IMapStep"/>, <see cref="IGroupStep"/> or <see cref="IReduceStep"/>.
/// </summary>
internal abstract class Step : IAsyncDisposable
{
    protected Step(StepDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Definition = definition;
    }

    public StepDefinition Definition { get; }

    public string Name => Definition.Name;

    /// <summary>
    /// The shape of the step when <c>group-by</c> is absent.
    /// </summary>
    public abstract StepShape NativeShape { get; }

    /// <summary>
    /// Whether <c>group-by</c> is allowed.
    /// </summary>
    public virtual bool SupportsGrouping => false;

    /// <summary>
    /// The entering files the step consumes when the definition has no <c>match</c>.
    /// </summary>
    public virtual GlobSet DefaultMatch => GlobSet.All;

    public StepShape EffectiveShape => Definition.GroupBy is null ? NativeShape : StepShape.Group;

    /// <summary>
    /// Called once before the step receives its first unit: parse options, start workers.
    /// </summary>
    public virtual ValueTask InitializeAsync(BuildContext context, CancellationToken ct) => default;

    /// <summary>
    /// Called once when the step is done: stop workers, delete temporary folders.
    /// </summary>
    public virtual ValueTask DisposeAsync() => default;
}

internal interface IMapStep
{
    ValueTask<FileList> ProcessAsync(VirtualFile file, StepContext context, CancellationToken ct);
}

internal interface IGroupStep
{
    ValueTask<StepOutput> ProcessGroupAsync(FileGroup group, StepContext context, CancellationToken ct);
}

internal interface IReduceStep
{
    /// <summary>
    /// Receives every consumed file, sorted by path.
    /// </summary>
    ValueTask<StepOutput> ProcessAllAsync(IReadOnlyList<VirtualFile> files, StepContext context, CancellationToken ct);
}
