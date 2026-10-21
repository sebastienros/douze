using System.Diagnostics;
using Douze.FileSystems;
using Douze.Nodes;
using Douze.Results;

namespace Douze;

internal sealed class SiteHelper
{
    internal static async Task<int> BuildSiteAsync(Options options)
    {
        foreach (var folders in new[] { options.IncludesFolders, options.DataFolders, options.ContentFolders })
        {
            foreach (var folder in folders)
            {
                if (!folder.IsDirectory)
                {
                    throw new ArgumentException($"Not a directory path: {folder}");
                }
            }
        }

        var files = (await options.SourceFileSystem.EnumerateFileSystemEntriesAsync(FPath.Root, true, null)).Where(x => !x.IsDirectory).OrderBy(x => x.Path).ToArray();

        var documents = files.Select(x => new SourceDocument(options.SourceFileSystem, x.Path)).ToList<IDocument>();

        // 1- Process data from all files

        foreach (var processor in options.DataProcessors)
        {
            var sw2 = Stopwatch.StartNew();

            var results = await processor.ProcessFileAsync(documents, options);

            documents.Clear();

            foreach (var result in results)
            {
                await result.ExecuteAsync(documents);

                if (!string.IsNullOrEmpty(result.Description))
                {
                    Console.WriteLine($"{result.Description} in {sw2.ElapsedMilliseconds} ms");
                }
            }
        }

        // 2- Group custom data by folder

        var dataByPath = new Dictionary<FPath, NodeObject>();

        // The site data cannot replace a property that is set at the page level

        var siteDataMergeSettings = new MergeSettings { MergeObjectHandling = MergeObjectHandling.Union };

        var filesByDirectory = documents.GroupBy(x => x.Path.GetDirectory()).ToDictionary(x => x.Key, x => x.ToArray());

        foreach (var entry in filesByDirectory)
        {
            var directoryName = entry.Key;
            var directoryFiles = entry.Value;

            var data = dataByPath[directoryName] = new NodeObject();

            foreach (var document in entry.Value)
            {
                if (document.DocumentType == DocumentTypes.Data)
                {
                    data.Merge(document.Data);
                }
            }
        }

        // The data from the data folder is associated to the root folder by convention

        if (!dataByPath.ContainsKey(FPath.Root))
        {
            dataByPath[FPath.Root] = new NodeObject();
        }

        foreach (var dataFolder in options.DataFolders)
        {
            if (dataByPath.TryGetValue(dataFolder.ToAbsolute(), out var globalData))
            {
                dataByPath[FPath.Root].Merge(globalData);
            }
        }

        // 2- Map: Create all new documents

        // Create data for each document

        foreach (var document in documents)
        {
            var dir = document.Path.GetDirectory();

            // Gather all data from each parent folder

            var folderDataObjects = new List<NodeObject>();

            while (dir != FPath.Empty)
            {
                if (dataByPath.TryGetValue(dir, out var folderData))
                {
                    folderDataObjects.Insert(0, folderData);
                }

                dir = dir.GetParentDirectory();
            }

            foreach (var folderData in folderDataObjects)
            {
                document.Data.Merge(folderData, siteDataMergeSettings);
            }
        }

        // Ignore templates in special folders

        documents = documents.Where(document =>
            !options.IncludesFolders.Any(includeFolder => document.Path.IsInDirectory(includeFolder.ToAbsolute()))
            && !options.DataFolders.Any(dataFolder => document.Path.IsInDirectory(dataFolder.ToAbsolute()))).ToList();

        foreach (var processor in options.ContentProcessors)
        {
            var sw2 = Stopwatch.StartNew();

            var results = await processor.ProcessFileAsync(documents, options);

            documents.Clear();

            foreach (var result in results)
            {
                await result.ExecuteAsync(documents);

                if (!string.IsNullOrEmpty(result.Description))
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

