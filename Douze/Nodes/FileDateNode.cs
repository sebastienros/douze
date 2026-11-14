using System.Diagnostics.CodeAnalysis;
using Douze.FileSystems;
using Douze.Workflow;

namespace Douze.Nodes;

/// <summary>
/// <c>page.date</c>: the modification time of a source file, held by reference. The node is hashed, stored and compared
/// by its source path only, so the metadata of a file whose date changed (a fresh checkout, a copy) keeps its hash, and
/// what does not read the date replays from the cache. Its value comes from the files of the build when a unit reads it
/// (<see cref="Read"/>), and the unit records that date as a dependency (<see cref="DependencyTracker.ReadFileDate"/>):
/// only the units that read it run again when it changes.
/// </summary>
internal sealed class FileDateNode : NodeValue
{
    // The date when the node was created: the value read outside a unit, or for a file the build does not list.
    private readonly DateTime _date;

    public FileDateNode(FPath source, DateTime date)
    {
        Source = source;
        _date = date;
    }

    /// <summary>
    /// The source file the date is the modification time of.
    /// </summary>
    public FPath Source { get; }

    /// <summary>
    /// The date the node was created with, without reading it: for the cache, which stores the node.
    /// </summary>
    internal DateTime CreatedDate => _date;

    /// <summary>
    /// The modification time of <see cref="Source"/> in the current build, recorded as read by the current unit.
    /// </summary>
    public DateTime Read() => DependencyTracker.Current is { } tracker ? tracker.ReadFileDate(Source, _date) : _date;

    public override T GetValue<T>()
    {
        return TryGetValue<T>(out var value) ? value : throw new InvalidOperationException($"Invalid conversion from {typeof(DateTime)} to {typeof(T)}");
    }

    public override bool TryGetValue<T>([NotNullWhen(true)] out T value)
    {
        if (Read() is T date)
        {
            value = date;
            return true;
        }

        value = default!;
        return false;
    }

    public override Node Clone() => new FileDateNode(Source, _date);

    public override bool Equals(Node? other) => other is FileDateNode date && date.Source == Source;

    public override int GetHashCode() => Source.GetHashCode();

    public override string ToString() => Read().ToString();
}
