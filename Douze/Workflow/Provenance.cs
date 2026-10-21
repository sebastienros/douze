using System.Collections.Immutable;

namespace Douze.Workflow;

/// <summary>
/// The step that produced a file and the refs of the files it was derived from, one level deep.
/// </summary>
internal sealed record Provenance(string Step, ImmutableArray<FileRef> Inputs)
{
    public const string SourceStep = "source";

    public static readonly Provenance Source = new(SourceStep, ImmutableArray<FileRef>.Empty);

    public bool IsSource => Step == SourceStep;
}
