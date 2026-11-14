using System.Collections.Frozen;
using Douze.FileSystems;

namespace Douze.Workflow;

/// <summary>
/// The modification time of every source file of a build, by path: what <c>page.date</c>
/// (<see cref="Nodes.FileDateNode"/>) reads, and what a cached result that read a date is checked against.
/// </summary>
internal sealed class SourceDates
{
    public static readonly SourceDates Empty = new(FrozenDictionary<FPath, DateTime>.Empty);

    private readonly FrozenDictionary<FPath, DateTime> _dates;

    private SourceDates(FrozenDictionary<FPath, DateTime> dates)
    {
        _dates = dates;
    }

    public static SourceDates Of(IEnumerable<VirtualFile> files)
    {
        var dates = new Dictionary<FPath, DateTime>();

        foreach (var file in files)
        {
            if (file.LastWriteTimeUtc is { } date)
            {
                dates[file.SourcePath ?? file.Path] = date;
            }
        }

        return new SourceDates(dates.ToFrozenDictionary());
    }

    public bool TryGet(FPath path, out DateTime date) => _dates.TryGetValue(path, out date);
}
