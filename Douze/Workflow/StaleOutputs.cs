using Douze.FileSystems;

namespace Douze.Workflow;

/// <summary>
/// Deletes the output files that a previous build wrote and this one did not: the pages of deleted or renamed sources,
/// the pages a pagination no longer has. A file is deleted only while it is as the previous build left it (its length
/// and modification time), so a file that something else wrote or changed since stays. Directories left empty are
/// deleted too, never the output folder itself.
/// </summary>
internal static class StaleOutputs
{
    /// <summary>
    /// Deletes what <paramref name="previous"/> lists and <paramref name="current"/> does not, and returns the deleted
    /// files, sorted.
    /// </summary>
    public static async Task<IReadOnlyList<FPath>> DeleteAsync(IFileSystem output, IEnumerable<(FPath Path, long Length, DateTime LastWriteTimeUtc)> previous, IReadOnlyList<(FPath Path, long Length, DateTime LastWriteTimeUtc)> current)
    {
        var written = new HashSet<FPath>(current.Select(x => x.Path));
        var stale = previous
            .Where(x => !written.Contains(x.Path))
            .GroupBy(x => x.Path)
            .OrderBy(x => x.Key)
            .ToArray();

        if (stale.Length == 0)
        {
            return [];
        }

        var deleted = new List<FPath>();

        foreach (var file in stale)
        {
            if (!(await output.GetFileEntryAsync(file.Key)).Exists)
            {
                continue;
            }

            var info = await output.GetFileMetadataAsync(file.Key);

            if (!file.Any(x => x.Length == info.Length && x.LastWriteTimeUtc == info.LastWriteTimeUtc))
            {
                continue;
            }

            await output.DeleteFileAsync(file.Key);
            deleted.Add(file.Key);
        }

        // Deepest first, so that a directory whose subdirectories were emptied is empty when its turn comes.
        var directories = new HashSet<FPath>();

        foreach (var path in deleted)
        {
            for (var directory = path.GetDirectory(); directory != FPath.Root && directory != FPath.Empty; directory = directory.GetParentDirectory())
            {
                directories.Add(directory);
            }
        }

        foreach (var directory in directories.OrderByDescending(x => x.FullName.Length).ThenBy(x => x))
        {
            await output.DeleteDirectoryIfEmptyAsync(directory);
        }

        return deleted;
    }
}
