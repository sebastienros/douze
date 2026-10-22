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
    /// What the results of the step depend on beyond its definition and its inputs, part of every result key: for a
    /// command, the hash of its <c>version</c> and of its <c>sources</c>. Read once <see cref="InitializeAsync"/> ran.
    /// </summary>
    public virtual Hash128 CacheVersion => Hash128.Empty;

    /// <summary>
    /// Whether the units of the step record what they read through the scopes (<see cref="ScopeSet.TryResolve"/>). A
    /// step that reads scopes without recording must return false: its results then depend on every scope of the step
    /// and are reused only when none changed. A command step reads no scopes at all, so it keeps the default.
    /// </summary>
    public virtual bool TracksDependencies => true;

    /// <summary>
    /// Whether the step reads the content of the files it consumes. A step that reads only their metadata (and emits
    /// none of their contents) is not run again when only a content changed: its result keys leave contents out.
    /// </summary>
    public virtual bool ReadsContent => true;

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

/// <summary>
/// A step that keeps state across its units, told about the units replayed from the cache instead of run.
/// </summary>
internal interface IReplayObserver
{
    /// <exception cref="DouzeException">The replayed unit fails, as it would have when run.</exception>
    void Replayed(IReadOnlyList<VirtualFile> inputs, IReadOnlyList<VirtualFile> outputs);
}

internal interface IReduceStep
{
    /// <summary>
    /// Receives every consumed file, sorted by path.
    /// </summary>
    ValueTask<StepOutput> ProcessAllAsync(IReadOnlyList<VirtualFile> files, StepContext context, CancellationToken ct);
}
