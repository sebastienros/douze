using System.Collections.Immutable;

namespace Douze.Workflow.Cache;

/// <summary>
/// The identity of a unit's result, used by the in-memory replay and the disk cache to find a previous result for
/// the same inputs. What the unit read besides its inputs (templates, data files, scopes) is not part of the key: it
/// is only known once the unit ran, and is kept with the result as its <see cref="DependencySet"/>, which a replay
/// checks against the current templates and scopes before reusing the result.
/// </summary>
internal static class ResultKey
{
    /// <summary>
    /// Combines the step name, its configuration hash, the input refs sorted by path, and the command version
    /// (<see cref="Hash128.Empty"/> for built-in steps). Without <paramref name="contents"/> (a step that reads only the
    /// metadata of its inputs, such as <c>collect</c>), the content hashes of the inputs are left out.
    /// </summary>
    public static Hash128 Compute(StepDefinition definition, ImmutableArray<FileRef> inputs, Hash128 commandVersion, bool contents = true)
    {
        var sorted = inputs.Sort(static (a, b) => a.Path.CompareTo(b.Path));
        var hashes = new Hash128[sorted.Length * 3 + 3];
        var i = 0;

        hashes[i++] = Hash128.OfText(definition.Name);
        hashes[i++] = definition.ConfigHash;

        foreach (var input in sorted)
        {
            hashes[i++] = Hash128.OfText(input.Path.FullName);
            hashes[i++] = contents ? input.ContentHash : Hash128.Empty;
            hashes[i++] = input.MetadataHash;
        }

        hashes[i++] = commandVersion;

        return Hash128.Combine(hashes.AsSpan(0, i));
    }
}
