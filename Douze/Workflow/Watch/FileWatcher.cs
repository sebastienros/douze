using System.Threading.Channels;
using Douze.FileSystems;

namespace Douze.Workflow.Watch;

/// <summary>
/// Watches the source folder and reports its changes in batches: the events of a batch are merged into a
/// <see cref="ChangeSet"/> once no event came for the debounce time (an editor saving a file raises several, a checkout
/// many). Paths are virtual (<c>/_content/index.md</c>); the paths <c>ignore</c> rejects (the output and cache folders
/// inside the source folder, <c>.git</c>) are dropped, so that writing the site does not start another build. A file
/// outside the source folder (a workflow file given with <c>--workflow</c>) can be watched too: it is reported by its
/// full disk path; any other path outside the source folder is dropped. The callbacks of the watchers never throw: an
/// event whose path can't be mapped asks for a rescan.
/// </summary>
internal sealed class FileWatcher : IDisposable
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(150);

    private readonly string _root;
    private readonly Func<FPath, bool> _ignore;
    private readonly TimeSpan _debounce;
    private readonly bool _start;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly HashSet<string> _files = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private readonly Timer _timer;
    private readonly Channel<ChangeSet> _batches = Channel.CreateUnbounded<ChangeSet>(new UnboundedChannelOptions { SingleReader = true });
    private ChangeSet _pending = new();

    /// <param name="root">The source folder.</param>
    /// <param name="ignore">Whether to drop the changes of a path.</param>
    /// <param name="debounce">How long to wait after an event for more.</param>
    /// <param name="start">Whether to watch the disk; tests report events themselves.</param>
    public FileWatcher(string root, Func<FPath, bool> ignore, TimeSpan debounce, bool start = true)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _ignore = ignore;
        _debounce = debounce;
        _start = start;
        _timer = new Timer(_ => Flush());

        if (start)
        {
            Watch(_root, filter: null);
        }
    }

    /// <summary>
    /// Also watches a file outside the source folder, reported by its full path.
    /// </summary>
    public void WatchFile(string path)
    {
        var fullPath = Path.GetFullPath(path);

        lock (_lock)
        {
            _files.Add(fullPath);
        }

        if (_start)
        {
            Watch(Path.GetDirectoryName(fullPath)!, Path.GetFileName(fullPath));
        }
    }

    private void Watch(string directory, string? filter)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = filter == null,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            InternalBufferSize = 64 * 1024,
        };

        if (filter != null)
        {
            watcher.Filter = filter;
        }

        watcher.Created += (_, e) => Report(ChangeKind.Added, e.FullPath);
        watcher.Changed += (_, e) => Report(ChangeKind.Modified, e.FullPath);
        watcher.Deleted += (_, e) => Report(ChangeKind.Deleted, e.FullPath);
        watcher.Renamed += (_, e) => ReportRename(e.OldFullPath, e.FullPath);
        watcher.Error += (_, _) => ReportLost();
        watcher.EnableRaisingEvents = true;

        _watchers.Add(watcher);
    }

    /// <summary>
    /// The next batch of changes, merged with the batches that completed since, once a batch completed.
    /// </summary>
    public async Task<ChangeSet> WaitAsync(CancellationToken ct)
    {
        var changes = await _batches.Reader.ReadAsync(ct);

        while (_batches.Reader.TryRead(out var more))
        {
            changes.Merge(more);
        }

        return changes;
    }

    internal void Report(ChangeKind kind, string fullPath)
    {
        if (!TryToVirtual(fullPath, out var mapped))
        {
            ReportLost();
            return;
        }

        if (mapped is not { } path)
        {
            return;
        }

        lock (_lock)
        {
            _pending.Add(path, kind);
        }

        Delay();
    }

    internal void ReportRename(string oldFullPath, string newFullPath)
    {
        if (!TryToVirtual(oldFullPath, out var from) | !TryToVirtual(newFullPath, out var to))
        {
            ReportLost();
            return;
        }

        if (from == null && to == null)
        {
            return;
        }

        lock (_lock)
        {
            if (from is { } a)
            {
                _pending.Add(a, ChangeKind.Deleted);
            }

            if (to is { } b)
            {
                _pending.Add(b, ChangeKind.Added);
            }
        }

        Delay();
    }

    internal void ReportLost()
    {
        lock (_lock)
        {
            _pending.AddRescan();
        }

        Delay();
    }

    private void Delay()
    {
        _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
    }

    private void Flush()
    {
        ChangeSet batch;

        lock (_lock)
        {
            if (_pending.IsEmpty)
            {
                return;
            }

            batch = _pending;
            _pending = new ChangeSet();
        }

        _batches.Writer.TryWrite(batch);
    }

    /// <summary>
    /// The virtual path of a disk path of the source folder, or its full path for a watched file outside it; null when
    /// ignored (the source folder itself, another path outside it). False when the path can't be mapped.
    /// </summary>
    private bool TryToVirtual(string? fullPath, out FPath? path)
    {
        path = null;

        try
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                return false;
            }

            // Resolves the "." and ".." segments an event may carry.
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));

            if (!fullPath.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                bool watched;

                lock (_lock)
                {
                    watched = _files.Contains(fullPath);
                }

                if (watched)
                {
                    path = new FPath(fullPath.Replace(Path.DirectorySeparatorChar, '/'));
                }

                return true;
            }

            var relative = new FPath(fullPath[_root.Length..].Replace(Path.DirectorySeparatorChar, '/'));
            path = _ignore(relative) ? (FPath?)null : relative;
            return true;
        }
        catch (Exception)
        {
            path = null;
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        _timer.Dispose();
        _batches.Writer.TryComplete();
    }
}
