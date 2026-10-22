using Douze.FileSystems;

namespace Douze.Workflow.Watch;

internal enum ChangeKind { Added, Modified, Deleted }

/// <summary>
/// The changes of the source folder since the previous build, by virtual path. The kind of a change is what the events
/// said, merged (an addition then a deletion is a deletion, a deletion then an addition a modification); a build does
/// not trust it: it reads the source folder as it is, and the kinds only describe the changes. A path may be a folder
/// (a folder renamed or deleted reports the folder alone, without its files).
/// </summary>
internal sealed class ChangeSet
{
    private readonly Dictionary<FPath, ChangeKind> _changes = [];

    /// <summary>
    /// Events were lost (the watcher's buffer overflowed): anything may have changed.
    /// </summary>
    public bool Rescan { get; private set; }

    public bool IsEmpty => _changes.Count == 0 && !Rescan;

    public int Count => _changes.Count;

    /// <summary>
    /// The changed paths and their kinds, sorted by path.
    /// </summary>
    public IReadOnlyList<(FPath Path, ChangeKind Kind)> Changes => [.. _changes.OrderBy(x => x.Key).Select(x => (x.Key, x.Value))];

    public void Add(FPath path, ChangeKind kind)
    {
        if (!_changes.TryGetValue(path, out var previous))
        {
            _changes[path] = kind;
            return;
        }

        _changes[path] = (previous, kind) switch
        {
            (ChangeKind.Added, ChangeKind.Modified) => ChangeKind.Added,
            (ChangeKind.Deleted, ChangeKind.Added) => ChangeKind.Modified,
            (ChangeKind.Deleted, ChangeKind.Modified) => ChangeKind.Modified,
            _ => kind,
        };
    }

    /// <summary>
    /// A rename is the deletion of the old path and the addition of the new one.
    /// </summary>
    public void Rename(FPath from, FPath to)
    {
        Add(from, ChangeKind.Deleted);
        Add(to, ChangeKind.Added);
    }

    /// <summary>
    /// Replaces the kind of a change with what the source folder says it is.
    /// </summary>
    public void Correct(FPath path, ChangeKind kind)
    {
        if (_changes.ContainsKey(path))
        {
            _changes[path] = kind;
        }
    }

    public void AddRescan() => Rescan = true;

    /// <summary>
    /// Adds the changes of <paramref name="other"/>, which happened after these.
    /// </summary>
    public void Merge(ChangeSet other)
    {
        foreach (var (path, kind) in other._changes)
        {
            Add(path, kind);
        }

        Rescan |= other.Rescan;
    }

    /// <summary>
    /// Whether <paramref name="path"/> changed, itself or as a file of a changed folder.
    /// </summary>
    public bool Contains(FPath path)
    {
        if (Rescan || _changes.ContainsKey(path))
        {
            return true;
        }

        return _changes.Keys.Any(x => path.FullName.Length > x.FullName.Length && path.FullName.StartsWith(x.FullName, StringComparison.Ordinal) && path.FullName[x.FullName.Length] == '/');
    }

    /// <summary>
    /// <c>_content/index.md changed</c>, <c>3 files changed</c>, or <c>files changed</c> after lost events.
    /// </summary>
    public override string ToString()
    {
        if (Rescan)
        {
            return "files changed";
        }

        if (_changes.Count == 1)
        {
            var (path, kind) = _changes.First();
            return $"{path.ToRelative()} {kind switch { ChangeKind.Added => "added", ChangeKind.Deleted => "deleted", _ => "changed" }}";
        }

        return $"{_changes.Count} files changed";
    }
}
