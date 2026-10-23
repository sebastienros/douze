using System.Collections.Concurrent;
using Douze.FileSystems;
using Douze.Workflow;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Douze.Markdown;

/// <summary>
/// Rewrites the relative links to <c>.md</c> files of a parsed document to the urls of their pages, with the default
/// permalink rules: from a page of <c>/_content/docs/</c>, <c>other.md</c> is <c>/docs/other/</c>, <c>index.md</c> is
/// <c>/docs/</c>, <c>../a/b.md#usage</c> is <c>/a/b/#usage</c>. Reference links are links too. A link whose target is
/// not a file of the content folder is left as it is, and the lookup is recorded (<see cref="DependencySet.Absent"/>),
/// so that creating the file renders the page again. Absolute urls, root relative paths, <c>mailto:</c>, anchors and
/// images are left as they are.
/// </summary>
/// <remarks>
/// A pass over the document between parsing and rendering rather than a Markdig extension: the existence of a target is
/// read from the source file system, which is asynchronous.
/// </remarks>
internal sealed class LinkRewriter
{
    private const string MarkdownExtension = ".md";

    private readonly IFileSystem _source;
    private readonly FPath _contentFolder;

    // Whether each target exists, read once per build (the source does not change during a build).
    private readonly ConcurrentDictionary<FPath, bool> _exists = new();

    public LinkRewriter(IFileSystem source, FPath contentFolder)
    {
        _source = source;
        _contentFolder = contentFolder;
    }

    /// <param name="document">The parsed document; its links are changed.</param>
    /// <param name="directory">The source folder of the page, which relative links start from.</param>
    /// <param name="deps">Records the targets that do not exist.</param>
    public async ValueTask RewriteAsync(MarkdownDocument document, FPath directory, DependencyTracker? deps)
    {
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (link.IsImage || link.Url is not { Length: > 0 } url || !TrySplit(url, out var path, out var suffix))
            {
                continue;
            }

            if (Resolve(directory, path) is not { } target || !target.FullName.StartsWith(_contentFolder.FullName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!await ExistsAsync(target))
            {
                deps?.RecordTemplate(target, DependencySet.Absent);
                continue;
            }

            link.Url = Url(target) + suffix;
        }
    }

    /// <summary>
    /// The url of the page of a Markdown file with the default permalink rules: the content folder removed,
    /// <c>name.md</c> as <c>name/</c>, <c>index.md</c> as its folder.
    /// </summary>
    private string Url(FPath target)
    {
        var relative = target.FullName[_contentFolder.FullName.Length..];
        var withoutExtension = relative[..^MarkdownExtension.Length];

        if (withoutExtension == "index" || withoutExtension.EndsWith("/index", StringComparison.Ordinal))
        {
            withoutExtension = withoutExtension[..^"index".Length];
            return "/" + withoutExtension;
        }

        return "/" + withoutExtension + "/";
    }

    private async ValueTask<bool> ExistsAsync(FPath path)
    {
        if (!_exists.TryGetValue(path, out var exists))
        {
            exists = (await _source.GetFileEntryAsync(path)).Exists;
            _exists[path] = exists;
        }

        return exists;
    }

    /// <summary>
    /// Splits a relative link to a Markdown file into its path (unescaped) and its query and fragment.
    /// </summary>
    internal static bool TrySplit(string url, out string path, out string suffix)
    {
        path = "";
        suffix = "";

        // Anchors, root relative paths, protocol relative and absolute urls (a scheme before any '/', '?' or '#').
        if (url[0] is '#' or '/' or '?' || HasScheme(url))
        {
            return false;
        }

        var end = url.AsSpan().IndexOfAny('?', '#');
        var rawPath = end < 0 ? url : url[..end];

        if (!rawPath.EndsWith(MarkdownExtension, StringComparison.Ordinal))
        {
            return false;
        }

        path = Uri.UnescapeDataString(rawPath);
        suffix = end < 0 ? "" : url[end..];
        return true;
    }

    private static bool HasScheme(string url)
    {
        var colon = url.IndexOf(':');

        if (colon <= 0 || url.AsSpan(0, colon).IndexOfAny('/', '?', '#') >= 0)
        {
            return false;
        }

        return char.IsAsciiLetter(url[0]);
    }

    /// <summary>
    /// The absolute path of <paramref name="relative"/> from <paramref name="directory"/>, or <c>null</c> when it
    /// climbs above the root.
    /// </summary>
    internal static FPath? Resolve(FPath directory, string relative)
    {
        var segments = new List<string>(directory.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries));

        foreach (var segment in relative.Split('/'))
        {
            switch (segment)
            {
                case "" or ".":
                    break;
                case "..":
                    if (segments.Count == 0)
                    {
                        return null;
                    }

                    segments.RemoveAt(segments.Count - 1);
                    break;
                default:
                    // Only a '..' segment climbs: a name can contain two dots ('v1..v2.md').
                    segments.Add(segment);
                    break;
            }
        }

        return segments.Count == 0 ? (FPath?)null : new FPath("/" + string.Join('/', segments));
    }
}
