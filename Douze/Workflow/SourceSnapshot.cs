using Douze.FileSystems;
using Douze.Workflow.Watch;

namespace Douze.Workflow;

/// <summary>
/// The files of the source folder with their length and modification time, and which root nodes of a graph read each
/// of them. A build scans the source folder into a snapshot; a watch session keeps it and applies the changes the
/// watcher reported (<see cref="ApplyAsync"/>), so that a rebuild neither lists nor stats the files that did not
/// change, nor matches them against the <c>inputs</c> of the roots again.
/// </summary>
internal sealed class SourceSnapshot
{
    private readonly Dictionary<FPath, (long Length, DateTime LastWriteTimeUtc)> _files;

    // The files some root reads, sorted, with the roots that read each (by index in the graph's roots with inputs), for
    // the graph and the excluded folders they were computed for.
    private (WorkflowGraph Graph, string Excluded, Entry[] Entries)? _matching;

    private SourceSnapshot(Dictionary<FPath, (long, DateTime)> files)
    {
        _files = files;
    }

    /// <summary>
    /// A file some root reads: the indexes of those roots in the nodes with <c>inputs</c>.
    /// </summary>
    public readonly record struct Entry(FPath Path, long Length, DateTime LastWriteTimeUtc, int[] Roots);

    public int Count => _files.Count;

    /// <summary>
    /// Lists every file of <paramref name="source"/> outside the <paramref name="excluded"/> folders (the output and
    /// cache folders, when they are inside the source folder) with its length and modification time.
    /// </summary>
    public static async Task<SourceSnapshot> ScanAsync(IFileSystem source, IReadOnlyList<FPath> excluded, int parallelism, CancellationToken ct)
    {
        var entries = await source.EnumerateFileSystemEntriesAsync(FPath.Root, recursive: true, filter: null);
        var paths = entries.Where(x => !x.IsDirectory).Select(x => x.Path.ToAbsolute()).Where(x => !excluded.Any(x.IsInDirectory)).Distinct().ToArray();
        var stats = new (long, DateTime)[paths.Length];

        await Parallel.ForAsync(0, paths.Length, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, async (i, _) =>
        {
            var info = await source.GetFileMetadataAsync(paths[i]);
            stats[i] = (info.Length, info.LastWriteTimeUtc);
        });

        var files = new Dictionary<FPath, (long, DateTime)>(paths.Length);

        for (var i = 0; i < paths.Length; i++)
        {
            files[paths[i]] = stats[i];
        }

        return new SourceSnapshot(files);
    }

    /// <summary>
    /// Updates the snapshot with the paths that changed: a path that is a file now is stat'ed again, a folder is listed
    /// again, and the files of a path that is neither are removed. A changed path may be a folder that was renamed or
    /// deleted: the watcher reports it without its files. Returns what each path really is to the snapshot (added,
    /// modified, deleted), which the events cannot tell reliably (FSEvents reports an edit of a file created recently
    /// as a creation); a path the snapshot never had and that is not there is left out.
    /// </summary>
    public async Task<IReadOnlyList<(FPath Path, ChangeKind Kind)>> ApplyAsync(IFileSystem source, IEnumerable<FPath> changed)
    {
        var listChanged = false;
        var kinds = new List<(FPath, ChangeKind)>();

        foreach (var path in changed)
        {
            var directory = new FPath(path.FullName.TrimEnd('/') + "/");
            var file = new FPath(path.FullName.TrimEnd('/'));

            if (file.IsFile && (await source.GetFileEntryAsync(file)).Exists)
            {
                var info = await source.GetFileMetadataAsync(file);
                var added = !_files.ContainsKey(file);
                listChanged |= added;
                _files[file] = (info.Length, info.LastWriteTimeUtc);
                kinds.Add((path, added ? ChangeKind.Added : ChangeKind.Modified));
                continue;
            }

            var wasFile = _files.Remove(file);
            var isDirectory = directory != FPath.Root && (await source.GetDirectoryEntryAsync(directory)).Exists;
            var had = wasFile;
            listChanged |= wasFile;

            // The files that were under the path (a folder renamed or deleted), and the ones under it now.
            if (!wasFile || isDirectory)
            {
                foreach (var under in _files.Keys.Where(x => x.IsInDirectory(directory)).ToArray())
                {
                    _files.Remove(under);
                    listChanged = true;
                    had = true;
                }
            }

            if (isDirectory || had)
            {
                kinds.Add((path, !isDirectory ? ChangeKind.Deleted : had ? ChangeKind.Modified : ChangeKind.Added));
            }

            if (isDirectory)
            {
                foreach (var entry in await source.EnumerateFileSystemEntriesAsync(directory, recursive: true, filter: null))
                {
                    var under = entry.Path.ToAbsolute();

                    if (entry.IsDirectory || !under.IsInDirectory(directory))
                    {
                        continue;
                    }

                    var info = await source.GetFileMetadataAsync(under);
                    _files[under] = (info.Length, info.LastWriteTimeUtc);
                    listChanged = true;
                }
            }
        }

        Refresh(listChanged);
        return kinds;
    }

    private void Refresh(bool listChanged)
    {
        if (listChanged)
        {
            _matching = null;
        }
        else if (_matching is { } matching)
        {
            // Same files, new stats: the matches stay.
            var entries = matching.Entries;

            for (var i = 0; i < entries.Length; i++)
            {
                var (length, date) = _files[entries[i].Path];
                entries[i] = entries[i] with { Length = length, LastWriteTimeUtc = date };
            }
        }
    }

    /// <summary>
    /// The files that a root of <paramref name="graph"/> reads (<c>roots</c>: the nodes with <c>inputs</c>), outside the
    /// excluded folders, sorted by path.
    /// </summary>
    public IReadOnlyList<Entry> Matching(WorkflowGraph graph, IReadOnlyList<GraphNode> roots, IReadOnlyList<FPath> excluded)
    {
        var excludedKey = string.Join('\n', excluded);

        if (_matching is { } matching && matching.Graph == graph && matching.Excluded == excludedKey)
        {
            return matching.Entries;
        }

        var entries = new List<Entry>(_files.Count);
        var indexes = new List<int>();

        foreach (var (path, (length, date)) in _files)
        {
            if (excluded.Any(path.IsInDirectory))
            {
                continue;
            }

            indexes.Clear();

            for (var i = 0; i < roots.Count; i++)
            {
                if (roots[i].Inputs!.IsMatch(path))
                {
                    indexes.Add(i);
                }
            }

            if (indexes.Count > 0)
            {
                entries.Add(new Entry(path, length, date, [.. indexes]));
            }
        }

        var sorted = entries.OrderBy(x => x.Path).ToArray();
        _matching = (graph, excludedKey, sorted);
        return sorted;
    }
}
