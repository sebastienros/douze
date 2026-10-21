using System.Collections.Immutable;

namespace Douze.Workflow;

/// <summary>
/// The identity of a unit's result, used by the in-memory replay and the disk cache.
/// </summary>
internal static class ResultKey
{
    /// <summary>
    /// Combines the step name, its configuration hash, the input refs sorted by path, the scopes hash when the step reads
    /// scopes, the recorded dependencies and the command version (<see cref="Hash128.Empty"/> for built-in steps).
    /// </summary>
    public static Hash128 Compute(StepDefinition definition, ImmutableArray<FileRef> inputs, Hash128? scopesHash, DependencySet dependencies, Hash128 commandVersion)
    {
        var sorted = inputs.Sort(static (a, b) => a.Path.CompareTo(b.Path));
        var hashes = new Hash128[sorted.Length * 3 + 6];
        var i = 0;

        hashes[i++] = Hash128.OfText(definition.Name);
        hashes[i++] = definition.ConfigHash;

        foreach (var input in sorted)
        {
            hashes[i++] = Hash128.OfText(input.Path.FullName);
            hashes[i++] = input.ContentHash;
            hashes[i++] = input.MetadataHash;
        }

        // A marker distinguishes "no scopes" from any scopes hash.
        hashes[i++] = scopesHash.HasValue ? Hash128.OfText("scopes") : Hash128.Empty;
        hashes[i++] = scopesHash ?? Hash128.Empty;
        hashes[i++] = dependencies.Hash;
        hashes[i++] = commandVersion;

        return Hash128.Combine(hashes.AsSpan(0, i));
    }
}
