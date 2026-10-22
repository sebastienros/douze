using Douze.FileSystems;
using Douze.Nodes;

namespace Douze.Workflow.Scheduling;

/// <summary>
/// The groups of variants (<see cref="FileVariant"/>: a page and its locale fallback copies) a map step that records its
/// own reads (<see cref="Step.RecordsOwnReads"/>) received. The first variant of a group to arrive is its leader and
/// runs; the others, its followers, wait for the leader's outcome and take its result (<see cref="Share"/>) when the
/// unit read none of the keys that differ between the leader and them, nor the path: a copy whose page does not read
/// its locale is not rendered again. A follower runs its own unit otherwise.
/// </summary>
/// <remarks>
/// Variants are grouped by everything a unit could read besides the keys of the group and the prefix of the path
/// (<see cref="KeyOf"/>), so a follower is the leader's file but for those. A group is forgotten once all its members
/// arrived; the groups whose members did not all reach the step are forgotten with the step.
/// </remarks>
internal sealed class VariantSharing
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Hash128, Group> _groups = [];

    /// <summary>
    /// Enters <paramref name="file"/> in the group of <paramref name="key"/>. Returns true when it leads the group: it
    /// runs, then completes the group (<see cref="Group.Complete"/>).
    /// </summary>
    public bool Enter(Hash128 key, VirtualFile file, out Group group)
    {
        lock (_lock)
        {
            if (!_groups.TryGetValue(key, out var existing))
            {
                _groups[key] = group = new Group(file);
                return true;
            }

            group = existing;

            if (++group.Arrived >= file.Variant!.Members)
            {
                _groups.Remove(key);
            }

            return false;
        }
    }

    /// <summary>
    /// The key of the group of variants of <paramref name="file"/>: the hash of its content, its path without the
    /// prefix of its variant, its folder, its content type, its dates, and every key of its own metadata but the keys of
    /// its variant. Null when its path does not start with the prefix (a step moved it).
    /// </summary>
    public static Hash128? KeyOf(VirtualFile file)
    {
        var variant = file.Variant!;
        var path = file.Path.FullName;

        if (!HasPrefix(path, variant.Prefix))
        {
            return null;
        }

        var own = file.Metadata.Own;
        var hashes = new List<Hash128>(own.Count * 2 + variant.Keys.Length + 8)
        {
            Hash128.OfText(path[variant.Prefix.Length..]),
            file.ContentHash,
            Hash128.OfText(file.Metadata.Directory.FullName),
            Hash128.OfText(file.ContentType ?? ""),
            Hash128.OfText(file.SourcePath?.FullName ?? ""),
            file.LastWriteTimeUtc is { } date ? new Hash128((ulong)date.Ticks, (ulong)date.Kind) : Hash128.Empty,
            new Hash128((ulong)variant.Members, 0),
        };

        foreach (var key in variant.Keys)
        {
            hashes.Add(Hash128.OfText(key));
        }

        hashes.Add(Hash128.Empty);

        foreach (var (key, value) in own.Where(x => !variant.Keys.Contains(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            hashes.Add(Hash128.OfText(key));
            hashes.Add(NodeHash.Of(value));
        }

        return Hash128.Combine([.. hashes]);
    }

    /// <summary>
    /// The outputs <paramref name="follower"/> gets from the outputs of its leader's unit, or null when it must run its
    /// own: when the unit read the path, all of the own metadata, or a key whose value differs between the two files.
    /// An output that carries the leader's values of those keys (its metadata derives from the leader's) gets the
    /// follower's, and the follower's prefix in its path; an output that has none of them (an image the unit added) is
    /// the follower's as it is. Any other output makes the follower run.
    /// </summary>
    public static FileList? Share(string step, VirtualFile leader, FileList outputs, OwnReads reads, VirtualFile follower)
    {
        if (reads.Path || reads.All || leader.Variant is not { } leaderVariant || follower.Variant is not { } followerVariant)
        {
            return null;
        }

        var leaderOwn = leader.Metadata.Own;
        var followerOwn = follower.Metadata.Own;
        var differing = new List<string>();

        foreach (var key in followerVariant.Keys.Union(leaderVariant.Keys))
        {
            if (!SameValue(leaderOwn, followerOwn, key))
            {
                if (reads.Keys.Contains(key))
                {
                    return null;
                }

                differing.Add(key);
            }
        }

        var shared = new VirtualFile[outputs.Count];

        for (var i = 0; i < outputs.Count; i++)
        {
            var output = outputs[i];
            var own = output.Metadata.Own;
            var carried = true;
            var absent = true;

            foreach (var key in differing)
            {
                var has = own.TryGetItem(key, out var value);
                absent &= !has;
                carried &= leaderOwn.TryGetItem(key, out var leaderValue) ? has && ReferenceEquals(value, leaderValue) : !has;
            }

            if (carried == absent)
            {
                // Both (nothing tells them apart) or neither.
                return null;
            }

            if (absent)
            {
                shared[i] = output;
                continue;
            }

            if (!HasPrefix(output.Path.FullName, leaderVariant.Prefix))
            {
                return null;
            }

            var metadata = output.Metadata.With(own =>
            {
                foreach (var key in differing)
                {
                    if (followerOwn.TryGetItem(key, out var value))
                    {
                        own[key] = value;
                    }
                    else
                    {
                        own.Remove(key);
                    }
                }
            });

            var path = new FPath(followerVariant.Prefix + output.Path.FullName[leaderVariant.Prefix.Length..]);
            shared[i] = new VirtualFile(path, output.Content, metadata, output.ContentType, new Provenance(step, [follower.Ref]), output.LastWriteTimeUtc, output.SourcePath, output.Variant == null ? null : followerVariant);
        }

        return FileList.Of(shared);
    }

    private static bool HasPrefix(string path, string prefix)
    {
        return prefix.Length == 0 || path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.Ordinal) && path[prefix.Length] == '/';
    }

    private static bool SameValue(NodeObject a, NodeObject b, string key)
    {
        var inA = a.TryGetItem(key, out var x);
        var inB = b.TryGetItem(key, out var y);

        return inA == inB && (!inA || ReferenceEquals(x, y) || NodeHash.Of(x) == NodeHash.Of(y));
    }

    /// <summary>
    /// A group of variants: its leader, and the followers that arrived before the leader's unit was done.
    /// </summary>
    internal sealed class Group(VirtualFile leader)
    {
        private readonly Lock _lock = new();
        private bool _done;
        private UnitOutcome? _outcome;
        private List<VirtualFile>? _parked = [];

        public VirtualFile Leader { get; } = leader;

        /// <summary>
        /// The members that arrived, the leader included; changed under the lock of the <see cref="VariantSharing"/>.
        /// </summary>
        public int Arrived { get; set; } = 1;

        /// <summary>
        /// Whether a follower goes on now, the leader being done, with the leader's outcome (null when it failed or was
        /// cancelled). Otherwise the follower is parked: the leader handles it once done.
        /// </summary>
        public bool TryFollow(VirtualFile file, out UnitOutcome? outcome)
        {
            lock (_lock)
            {
                outcome = _outcome;

                if (_done)
                {
                    return true;
                }

                _parked!.Add(file);
                return false;
            }
        }

        /// <summary>
        /// Records the outcome of the leader's unit, and returns the parked followers.
        /// </summary>
        public IReadOnlyList<VirtualFile> Complete(UnitOutcome? outcome)
        {
            lock (_lock)
            {
                _done = true;
                _outcome = outcome;
                var parked = _parked!;
                _parked = null;
                return parked;
            }
        }
    }
}
