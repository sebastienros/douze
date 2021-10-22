using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Douze.FileSystems;

namespace Douze;

internal class SiteHelper
{
    internal static async Task<int> BuildSiteAsync(Options options)
    {
        var files = (await options.SourceFileSystem.EnumerateFileSystemEntriesAsync(FPath.Root, true, null)).Where(x => !x.IsDirectory).OrderBy(x => x.Path).ToArray();

        // Process all data files and store the values per folder

        var dataByPath = new ConcurrentDictionary<FPath, JsonObject>();

        // Data files are computed in parallel by directory

        var filesByDirectory = files.GroupBy(x => x.Path.GetDirectory()).ToDictionary(x => x.Key, x => x.ToArray());

        await Parallel.ForEachAsync(filesByDirectory, new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, async (entry, cancellationToken) =>
        {
            foreach (var file in entry.Value)
            {
                var extension = Path.GetExtension(file.Path.FullName) ?? "";

                if (options.DataMappings.TryGetValue(extension, out var processor))
                {
                    var directoryName = entry.Key;
                    var fileEntry = await options.SourceFileSystem.GetFileEntryAsync(file.Path);

                    // Don't process data files from the root folder, use the _data folder instead
                    if (directoryName == FPath.Root)
                    {
                        continue;
                    }

                    if (!dataByPath.TryGetValue(directoryName, out var data))
                    {
                        dataByPath[directoryName] = data = new JsonObject();
                    }

                    var sw = Stopwatch.StartNew();

                    await foreach (var result in processor.ProcessFileAsync(fileEntry, options, data))
                    {
                        if (!result.FilePath.IsEmpty)
                        {
                            var directory = result.FilePath.GetDirectory();

                            if (directory != FPath.Root)
                            {
                                await options.OutputFileSystem.CreateDirectoryAsync(directory);
                            }

                            await using var stream = await options.OutputFileSystem.CreateFileAsync(result.FilePath);
                            await result.SaveFileAsync(stream);
                        }

                        Console.WriteLine($"{result.Description} in {sw.ElapsedMilliseconds} ms");
                    }
                }
            }
        });

        if (!dataByPath.TryGetValue(options.DataFolder.ToAbsolute(), out var globalData))
        {
            globalData = new JsonObject();
        }

        dataByPath[FPath.Root] = globalData;

        // Process all content files

        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, async (file, cancellationToken) =>
        {
            // Ignore templates in special folders

            if (file.Path.IsInDirectory(options.IncludesFolder.ToAbsolute()) ||
                file.Path.IsInDirectory(options.DataFolder.ToAbsolute()))
            {
                return;
            }

            var extension = file.Path.GetExtensionWithDot() ?? "";

            // Apply corresponding file processor

            if (options.ContentMappings.TryGetValue(extension, out var processor))
            {
                // Gather all data from each parent folder

                var fileEntry = await options.SourceFileSystem.GetFileEntryAsync(file.Path);
                var directoryEntry = await options.SourceFileSystem.GetDirectoryEntryAsync(file.Path.GetDirectory());

                var data = JsonHelper.Clone(globalData)?.AsObject() ?? new JsonObject();

                var folderDataObjects = new List<JsonObject>();

                while (directoryEntry.Exists)
                {
                    if (dataByPath.TryGetValue(directoryEntry.Path, out var folderData))
                    {
                        folderDataObjects.Insert(0, folderData);
                    }

                    if (directoryEntry.Path == FPath.Root)
                    {
                        break;
                    }

                    directoryEntry = await options.SourceFileSystem.GetDirectoryEntryAsync(FileSystemHelper.GetParentDirectory(directoryEntry.Path));
                }

                foreach (var folderData in folderDataObjects)
                {
                    JsonHelper.Merge(data, folderData);
                }

                var sw = Stopwatch.StartNew();

                await foreach (var result in processor.ProcessFileAsync(fileEntry, options, data))
                {
                    if (!result.FilePath.IsEmpty)
                    {
                        var directory = result.FilePath.GetDirectory();

                        await options.OutputFileSystem.CreateDirectoryAsync(directory);

                        await using var stream = await options.OutputFileSystem.CreateFileAsync(result.FilePath);
                        await result.SaveFileAsync(stream);
                    }

                    Console.WriteLine($"{result.Description} in {sw.ElapsedMilliseconds} ms");
                }
            }
        });

        return 0;
    }

}

