using System.Collections.Immutable;
using Douze.FileSystems;
using Douze.Workflow.Watch;

namespace Douze.Workflow;

/// <summary>
/// The part of the source folder a build reads: the folders in which the <c>inputs</c> of its roots can match files
/// (the literal folder each pattern starts with, <c>/</c> for a set that can match anywhere) and the files of patterns
/// without wildcard. A snapshot lists them only: the other folders (<c>node_modules</c>, an old output folder) are
/// never walked.
/// </summary>
internal sealed class SourceScope
{
    public static readonly SourceScope Everything = new([FPath.Root], []);

    private SourceScope(ImmutableArray<FPath> folders, ImmutableArray<FPath> files)
    {
        Folders = folders;
        Files = files;
    }

    /// <summary>
    /// The folders listed with everything under them, sorted, none inside another.
    /// </summary>
    public ImmutableArray<FPath> Folders { get; }

    /// <summary>
    /// The files outside <see cref="Folders"/> that a pattern names, sorted.
    /// </summary>
    public ImmutableArray<FPath> Files { get; }

    /// <summary>
    /// The scope of the files that <paramref name="sets"/> can match.
    /// </summary>
    public static SourceScope Of(IEnumerable<GlobSet> sets)
    {
        var folders = new List<string>();
        var files = new List<string>();

        foreach (var set in sets)
        {
            if (set.CanMatchAnything)
            {
                return Everything;
            }

            foreach (var glob in set.Globs.Where(x => !x.Negated))
            {
                if (glob.LiteralPath is { } literal)
                {
                    files.Add(literal);
                }
                else
                {
                    folders.Add(glob.FixedPrefix);
                }
            }
        }

        if (folders.Contains("/"))
        {
            return Everything;
        }

        var kept = new List<string>();

        foreach (var folder in folders.Distinct().Order(StringComparer.Ordinal))
        {
            if (!kept.Any(x => folder.StartsWith(x, StringComparison.Ordinal)))
            {
                kept.Add(folder);
            }
        }

        var keptFiles = files.Distinct().Where(x => !kept.Any(f => x.StartsWith(f, StringComparison.Ordinal))).Order(StringComparer.Ordinal);

        return new([.. kept.Select(x => new FPath(x))], [.. keptFiles.Select(x => new FPath(x))]);
    }

    /// <summary>
    /// Whether <paramref name="path"/>, a file or a folder, is in the scope.
    /// </summary>
    public bool Contains(FPath path)
    {
        return Folders.Any(x => path.FullName.StartsWith(x.FullName, StringComparison.Ordinal)) || Files.Contains(path);
    }

    /// <summary>
    /// Whether a change of <paramref name="path"/> (a file, or a folder created, renamed or deleted) can change the
    /// files of the scope: the path is in it, or holds a part of it.
    /// </summary>
    public bool Touches(FPath path)
    {
        var folder = path.FullName.TrimEnd('/') + "/";

        return Contains(path)
            || Folders.Any(x => x.FullName.StartsWith(folder, StringComparison.Ordinal))
            || Files.Any(x => x.FullName.StartsWith(folder, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether every file of <paramref name="other"/> is in this scope.
    /// </summary>
    public bool Covers(SourceScope other) => other.Folders.All(Contains) && other.Files.All(Contains);

    public override string ToString() => string.Join(", ", Folders.Concat(Files));
}

/// <summary>
/// The files of the source folder with their length and modification time, and which root nodes of a graph read each
/// of them. A build scans the source folder into a snapshot; a watch session keeps it and applies the changes the
/// watcher reported (<see cref="ApplyAsync"/>), so that a rebuild neither lists nor stats the files that did not
/// change, nor matches them against the <c>inputs</c> of the roots again.
/// </summary>
internal sealed class SourceSnapshot
{
    private readonly Dictionary<FPath, (long Length, DateTime LastWriteTimeUtc)> _files;
    private readonly ImmutableArray<FPath> _excluded;

    // The files some root reads, sorted, with the roots that read each (by index in the graph's roots with inputs), for
    // the graph and the excluded folders they were computed for.
    private (WorkflowGraph Graph, string Excluded, Entry[] Entries)? _matching;

    private SourceSnapshot(Dictionary<FPath, (long, DateTime)> files, SourceScope scope, ImmutableArray<FPath> excluded)
    {
        _files = files;
        Scope = scope;
        _excluded = excluded;
    }

    /// <summary>
    /// The part of the source folder the snapshot lists.
    /// </summary>
    public SourceScope Scope { get; }

    /// <summary>
    /// Whether the snapshot lists every file of <paramref name="scope"/>, outside the same excluded folders: a watch
    /// session keeps it while the workflow reads no other folder.
    /// </summary>
    public bool Covers(SourceScope scope, IReadOnlyList<FPath> excluded)
    {
        return Scope.Covers(scope) && _excluded.SequenceEqual(excluded);
    }

    /// <summary>
    /// A file some root reads: the indexes of those roots in the nodes with <c>inputs</c>.
    /// </summary>
    public readonly record struct Entry(FPath Path, long Length, DateTime LastWriteTimeUtc, int[] Roots);

    public int Count => _files.Count;

    /// <summary>
    /// Lists every file of <paramref name="scope"/> outside the <paramref name="excluded"/> folders (the output, cache
    /// and <c>.douze</c> folders, when they are inside the source folder) with its length and modification time.
    /// </summary>
    public static async Task<SourceSnapshot> ScanAsync(IFileSystem source, SourceScope scope, IReadOnlyList<FPath> excluded, int parallelism, CancellationToken ct)
    {
        var paths = (await ListAsync(source, scope, excluded)).ToArray();
        var stats = new (long, DateTime)?[paths.Length];

        await Parallel.ForAsync(0, paths.Length, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, async (i, _) =>
        {
            stats[i] = await StatAsync(source, paths[i]);
        });

        var files = new Dictionary<FPath, (long, DateTime)>(paths.Length);

        for (var i = 0; i < paths.Length; i++)
        {
            if (stats[i] is { } stat)
            {
                files[paths[i]] = stat;
            }
        }

        return new SourceSnapshot(files, scope, [.. excluded]);
    }

    /// <summary>
    /// The files of <paramref name="scope"/> outside the <paramref name="excluded"/> folders, which are not walked; a
    /// file a pattern names is listed whether it exists or not.
    /// </summary>
    public static async Task<IReadOnlyList<FPath>> ListAsync(IFileSystem source, SourceScope scope, IReadOnlyList<FPath> excluded)
    {
        var paths = new HashSet<FPath>();

        foreach (var folder in scope.Folders)
        {
            if (IsExcluded(folder, excluded))
            {
                continue;
            }

            foreach (var entry in await source.EnumerateFileSystemEntriesAsync(folder, recursive: true, filter: x => !IsExcluded(new FPath(x.TrimEnd('/') + "/"), excluded)))
            {
                var path = entry.Path.ToAbsolute();

                // A file system may list more than the folder (the memory one lists everything).
                if (!entry.IsDirectory && path.FullName.StartsWith(folder.FullName, StringComparison.Ordinal) && !IsExcluded(path, excluded))
                {
                    paths.Add(path);
                }
            }
        }

        foreach (var file in scope.Files)
        {
            if (!IsExcluded(file, excluded))
            {
                paths.Add(file);
            }
        }

        return [.. paths];
    }

    private static bool IsExcluded(FPath path, IReadOnlyList<FPath> excluded) => excluded.Any(x => path.FullName.StartsWith(x.FullName, StringComparison.Ordinal));

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

            // Outside the folders the snapshot lists (a template, the output folder): nothing to update.
            if (!Scope.Touches(path) || IsExcluded(directory, _excluded))
            {
                continue;
            }

            if (file.IsFile && Scope.Contains(file) && await StatAsync(source, file) is { } stat)
            {
                var added = !_files.ContainsKey(file);
                listChanged |= added;
                _files[file] = stat;
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
                foreach (var entry in await source.EnumerateFileSystemEntriesAsync(directory, recursive: true, filter: x => !IsExcluded(new FPath(x.TrimEnd('/') + "/"), _excluded)))
                {
                    var under = entry.Path.ToAbsolute();

                    if (entry.IsDirectory || !under.IsInDirectory(directory) || !Scope.Contains(under) || IsExcluded(under, _excluded))
                    {
                        continue;
                    }

                    if (await StatAsync(source, under) is { } underStat)
                    {
                        _files[under] = underStat;
                        listChanged = true;
                    }
                }
            }
        }

        Refresh(listChanged);
        return kinds;
    }

    /// <summary>
    /// The length and modification time of a file, or null when it is not there (anymore: an editor saving a file may
    /// write a temporary file and rename it; the watcher reports both).
    /// </summary>
    private static async ValueTask<(long, DateTime)?> StatAsync(IFileSystem source, FPath path)
    {
        try
        {
            var info = await source.GetFileMetadataAsync(path);
            return (info.Length, info.LastWriteTimeUtc);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
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
