using System.Collections.Concurrent;
using System.Diagnostics;
using Douze.FileSystems;
using Douze.Nodes;
using Douze.Results;

namespace Douze;

internal class SiteHelper
{
    internal static async Task<int> BuildSiteAsync(Options options)
    {
        if (!options.IncludesFolder.IsDirectory)
        {
            throw new ArgumentException($"Not a directory path: {options.IncludesFolder}");
        }

        if (!options.DataFolder.IsDirectory)
        {
            throw new ArgumentException($"Not a directory path: {options.DataFolder}");
        }

        var files = (await options.SourceFileSystem.EnumerateFileSystemEntriesAsync(FPath.Root, true, null)).Where(x => !x.IsDirectory).OrderBy(x => x.Path).ToArray();

        var documents = files.Select(x => new SourceDocument(options.SourceFileSystem, x.Path)).ToList<IDocument>();

        // Process all data files and store the values per folder

        var dataByPath = new ConcurrentDictionary<FPath, NodeObject>();

        var filesByDirectory = documents.GroupBy(x => x.Path.GetDirectory()).ToDictionary(x => x.Key, x => x.ToArray());

        var dataProcessorsResult = new List<IDocument>();

        foreach (var entry in filesByDirectory)
        {
            foreach (var processor in options.DataProcessors)
            {
                var directoryName = entry.Key;

                // Don't process data files from the root folder, use the _data folder instead
                if (directoryName == FPath.Root)
                {
                    continue;
                }

                if (!dataByPath.TryGetValue(directoryName, out var data))
                {
                    dataByPath[directoryName] = data = new NodeObject();
                }

                var lsw = Stopwatch.StartNew();

                foreach (var result in await processor.ProcessFileAsync(entry.Value, options))
                {
                    await result.ExecuteAsync(dataProcessorsResult);

                    if (!String.IsNullOrEmpty(result.Description))
                    {
                        Console.WriteLine($"{result.Description} in {lsw.ElapsedMilliseconds} ms");
                    }
                }

                foreach (var document in entry.Value)
                {
                    data.Merge(document.Data);
                }
            }
        }

        // The data from the data folder is associated to the root folder by convention

        if (!dataByPath.TryGetValue(options.DataFolder.ToAbsolute(), out var globalData))
        {
            globalData = new NodeObject();
        }

        dataByPath[FPath.Root] = globalData;

        // 2- Map: Create all new documents

        // Create data for each document

        foreach (var document in documents)
        {
            var dir = document.Path.GetDirectory();

            // Gather all data from each parent folder

            var data = globalData.Clone()?.AsObject() ?? new NodeObject();

            var folderDataObjects = new List<NodeObject>();

            while (dir != FPath.Root)
            {
                if (dataByPath.TryGetValue(dir, out var folderData))
                {
                    folderDataObjects.Insert(0, folderData);
                }

                dir = dir.GetParentDirectory();
            }

            foreach (var folderData in folderDataObjects)
            {
                data.Merge(folderData);
            }

            document.Data.Merge(data);
        }

        var semaphore = new SemaphoreSlim(1);

        // Ignore templates in special folders

        documents = documents.Where(document =>
            !document.Path.IsInDirectory(options.IncludesFolder.ToAbsolute())
            && !document.Path.IsInDirectory(options.DataFolder.ToAbsolute())).ToList();

        foreach (var processor in options.ContentProcessors)
        {
            var sw2 = Stopwatch.StartNew();

            var results = await processor.ProcessFileAsync(documents, options);

            documents.Clear();

            foreach (var result in results)
            {
                await result.ExecuteAsync(documents);

                if (!String.IsNullOrEmpty(result.Description))
                {
                    Console.WriteLine($"{result.Description} in {sw2.ElapsedMilliseconds} ms");
                }
            }
        }

        // 3- Reduce: Alter documents based on all results

        var sw = Stopwatch.StartNew();

        // TODO: Apply filters before saving documents

        await Parallel.ForEachAsync(documents, new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, async (document, cancellationToken) =>
        {
            await document.SaveFileAsync();
        });

        return 0;
    }

}

