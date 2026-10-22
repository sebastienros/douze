using System.Collections.Concurrent;
using Douze.FileSystems;
using Douze.Workflow.Cache;

namespace Douze.Workflow.Scheduling;

/// <summary>
/// What the scheduler needs to replay units from the disk cache: the cache, and the checks of the dependencies of
/// cached results against the current build. A dependency set is checked once per step (the scopes are the step's),
/// except the written files, checked per unit.
/// </summary>
internal sealed class ReplayState
{
    private readonly BuildContext _context;
    private readonly ConcurrentDictionary<(string Step, Hash128 Dependencies), Task<bool>> _checked = new();
    private readonly ConcurrentDictionary<FPath, Lazy<Task<Hash128>>> _templates = new();

    public ReplayState(BuildCache cache, BuildContext context)
    {
        Cache = cache;
        _context = context;
    }

    public BuildCache Cache { get; }

    /// <summary>
    /// Whether every dependency of a cached result is unchanged: each template has its hash, each lookup resolves to the
    /// same value in <paramref name="scopes"/> (or the same part of it, for what a template read), each written file is
    /// in the output folder as it was written.
    /// </summary>
    public async ValueTask<bool> IsUnchangedAsync(string step, ScopeSet scopes, DependencySet dependencies, CancellationToken ct)
    {
        if (dependencies.IsEmpty)
        {
            return true;
        }

        if (!dependencies.Written.IsEmpty)
        {
            return await CheckAsync(scopes, dependencies).WaitAsync(ct);
        }

        return await _checked.GetOrAdd((step, dependencies.Hash), static (_, state) => state.This.CheckAsync(state.Scopes, state.Dependencies), (This: this, Scopes: scopes, Dependencies: dependencies)).WaitAsync(ct);
    }

    private async Task<bool> CheckAsync(ScopeSet scopes, DependencySet dependencies)
    {
        foreach (var lookup in dependencies.Lookups)
        {
            var hash = lookup.Access == null ? scopes.ResolveHash(lookup.Directory, lookup.Key) : scopes.ProjectHash(lookup.Directory, lookup.Key, lookup.Access);

            if (hash != lookup.Hash)
            {
                return false;
            }
        }

        foreach (var (path, hash) in dependencies.Templates)
        {
            if (await TemplateHashAsync(path) != hash)
            {
                return false;
            }
        }

        foreach (var (path, length, lastWriteTimeUtc) in dependencies.Written)
        {
            if (!(await _context.Output.GetFileEntryAsync(path)).Exists)
            {
                return false;
            }

            var info = await _context.Output.GetFileMetadataAsync(path);

            if (info.Length != length || info.LastWriteTimeUtc != lastWriteTimeUtc)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The hash of a template file of the source folder as units that run would see it, or
    /// <see cref="DependencySet.Absent"/>: the template loader's when it read the file already (a watch session keeps it
    /// across rebuilds), otherwise as it is now, read once per build.
    /// </summary>
    private Task<Hash128> TemplateHashAsync(FPath path)
    {
        return _templates.GetOrAdd(path, static (path, context) => new Lazy<Task<Hash128>>(async () =>
        {
            if (context.Templates.TryGetHash(path, out var known))
            {
                return known;
            }

            if (!(await context.Source.GetFileEntryAsync(path)).Exists)
            {
                return DependencySet.Absent;
            }

            await using var stream = await context.Source.OpenFileAsync(path);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            return Hash128.Of(memory.GetBuffer().AsSpan(0, (int)memory.Length));
        }, LazyThreadSafetyMode.ExecutionAndPublication), _context).Value;
    }
}
