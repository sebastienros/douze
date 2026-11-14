using System.Collections.Immutable;
using Douze.FileSystems;

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
    /// <paramref name="sources"/>, in the order of <paramref name="inputs"/>, are the paths of the source files the inputs
    /// come from (<see cref="VirtualFile.SourcePath"/>), which a step can read (<c>inputPath</c> of <c>collect</c>) and
    /// which can change without changing the path of the input. The dates of the source files are not part of the key:
    /// a fresh checkout gives every file a new one. A unit that reads one (<c>page.date</c>,
    /// <see cref="Nodes.FileDateNode"/>) records it in its <see cref="DependencySet"/> instead.
    /// </summary>
    public static Hash128 Compute(StepDefinition definition, ImmutableArray<FileRef> inputs, Hash128 commandVersion, bool contents = true, IReadOnlyList<FPath?>? sources = null)
    {
        var order = Enumerable.Range(0, inputs.Length).ToArray();
        Array.Sort(order, (a, b) => inputs[a].Path.CompareTo(inputs[b].Path));

        var hashes = new Hash128[inputs.Length * 4 + 3];
        var i = 0;

        hashes[i++] = Hash128.OfText(definition.Name);
        hashes[i++] = definition.ConfigHash;

        foreach (var index in order)
        {
            var input = inputs[index];
            hashes[i++] = Hash128.OfText(input.Path.FullName);
            hashes[i++] = contents ? input.ContentHash : Hash128.Empty;
            hashes[i++] = input.MetadataHash;
            hashes[i++] = sources?[index] is { } source ? Hash128.OfText(source.FullName) : Hash128.Empty;
        }

        hashes[i++] = commandVersion;

        return Hash128.Combine(hashes.AsSpan(0, i));
    }
}
