using Douze.FileSystems;

namespace Douze.Workflow.Steps;

internal static class StepPaths
{
    /// <summary>
    /// Normalizes a folder option (<c>_content</c>, <c>/_content/</c>, <c>.</c>) to an absolute directory path.
    /// </summary>
    public static FPath Directory(string value)
    {
        var trimmed = value.Trim().Trim('/');

        return trimmed is "" or "." ? FPath.Root : new FPath("/" + trimmed + "/");
    }

    /// <summary>
    /// Removes <paramref name="root"/> from the start of <paramref name="path"/>: <c>/_content/a/index.html</c> becomes
    /// <c>/a/index.html</c>. Paths outside the root are returned as is.
    /// </summary>
    public static FPath StripRoot(FPath path, FPath root)
    {
        if (root == FPath.Root || !path.FullName.StartsWith(root.FullName, StringComparison.Ordinal))
        {
            return path;
        }

        return new FPath("/" + path.FullName[root.FullName.Length..]);
    }
}
