using Douze.FileSystems;

namespace Douze.Workflow;

/// <summary>
/// An ordered list of globs. The last matching pattern decides; a set whose first pattern is negated starts
/// from "everything". <c>["content/**", "!**/*.yml"]</c> matches everything under <c>content/</c> except YAML files.
/// </summary>
internal sealed class GlobSet
{
    /// <summary>
    /// Matches every path.
    /// </summary>
    public static readonly GlobSet All = new([], matchAll: true);

    private readonly Glob[] _globs;
    private readonly bool _matchAll;

    private GlobSet(Glob[] globs, bool matchAll)
    {
        _globs = globs;
        _matchAll = matchAll;
        FixedPrefix = ComputeFixedPrefix(globs);
    }

    public IReadOnlyList<Glob> Globs => _globs;

    /// <exception cref="GlobFormatException">A pattern is invalid.</exception>
    public static GlobSet Parse(IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        return new GlobSet(patterns.Select(Glob.Parse).ToArray(), matchAll: false);
    }

    public static GlobSet Parse(params string[] patterns) => Parse((IEnumerable<string>)patterns);

    public bool IsMatch(FPath path)
    {
        if (_matchAll)
        {
            return true;
        }

        if (_globs.Length == 0)
        {
            return false;
        }

        var result = _globs[0].Negated;

        foreach (var glob in _globs)
        {
            // Only a pattern that can change the result needs to be evaluated.
            if (result == glob.Negated && glob.IsMatch(path))
            {
                result = !glob.Negated;
            }
        }

        return result;
    }

    /// <summary>
    /// The longest directory prefix shared by all positive patterns (<c>/content/</c>), used to narrow enumeration.
    /// <c>null</c> when the set has no positive pattern, so that any path can match.
    /// </summary>
    public string? FixedPrefix { get; }

    public override string ToString() => _matchAll ? "**" : string.Join(", ", _globs.Select(x => x.Pattern));

    private static string? ComputeFixedPrefix(Glob[] globs)
    {
        if (globs.Length == 0 || globs[0].Negated)
        {
            return null;
        }

        string? prefix = null;

        foreach (var glob in globs)
        {
            if (glob.Negated)
            {
                continue;
            }

            prefix = prefix == null ? glob.FixedPrefix : CommonDirectoryPrefix(prefix, glob.FixedPrefix);
        }

        return prefix;
    }

    private static string CommonDirectoryPrefix(string a, string b)
    {
        var length = Math.Min(a.Length, b.Length);
        var lastSlash = 0;

        for (var i = 0; i < length && a[i] == b[i]; i++)
        {
            if (a[i] == '/')
            {
                lastSlash = i;
            }
        }

        return a[..(lastSlash + 1)];
    }
}
