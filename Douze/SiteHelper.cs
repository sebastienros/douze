using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Zio;

namespace Douze;

internal class SiteHelper
{
    internal static async Task<int> BuildSiteAsync(Options options)
    {
        var files = options.SourceFileSystem.EnumerateItems(UPath.Root, SearchOption.AllDirectories).Where(x => !x.IsDirectory).OrderBy(x => x.Path).ToArray();
        
        // Process all data files and store the values per folder

        var dataByPath = new ConcurrentDictionary<UPath, JsonObject>();

        // Data files are computed in parallel by directory

        var filesByDirectory = files.GroupBy(x => x.Path.GetDirectory()).ToDictionary(x => x.Key, x => x.ToArray());

        await Parallel.ForEachAsync(filesByDirectory, new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, async (entry, cancellationToken) =>
        {
            foreach (var file in entry.Value)
            {
                var extension = file.Path.GetExtensionWithDot() ?? "";

                if (options.DataMappings.TryGetValue(extension, out var processor))
                {
                    var directoryName = entry.Key;
                    var fileEntry = options.SourceFileSystem.GetFileEntry(file.Path);

                    // Don't process data files from the root folder, use the _data folder instead
                    if (directoryName == "")
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

                            if (directory != UPath.Root)
                            {
                                options.OutputFileSystem.CreateDirectory(directory);
                            }

                            await using var stream = options.OutputFileSystem.CreateFile(result.FilePath);
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

        dataByPath[UPath.Root] = globalData;

        // Process all content files

        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, async (file, cancellationToken) =>
        {
            var extension = file.Path.GetExtensionWithDot() ?? "";

            // Apply corresponding file processor

            if (options.ContentMappings.TryGetValue(extension, out var processor))
            {
                // Gather all data from each parent folder

                var fileEntry = options.SourceFileSystem.GetFileEntry(file.Path);
                var directoryEntry = fileEntry.Directory;

                var data = JsonHelper.Clone(globalData)?.AsObject() ?? new JsonObject();

                var folderDataObjects = new List<JsonObject>();

                while (directoryEntry!.Exists)
                {
                    if (dataByPath.TryGetValue(directoryEntry.Path, out var folderData))
                    {
                        folderDataObjects.Insert(0, folderData);
                    }

                    if (directoryEntry.Path == UPath.Root)
                    {
                        break;
                    }

                    directoryEntry = directoryEntry.Parent;
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

                        if (directory != UPath.Root)
                        {
                            options.OutputFileSystem.CreateDirectory(directory);
                        }

                        await using var stream = options.OutputFileSystem.CreateFile(result.FilePath);
                        await result.SaveFileAsync(stream);
                    }

                    Console.WriteLine($"{result.Description} in {sw.ElapsedMilliseconds} ms");
                }
            }
        });

        return 0;
    }

}

