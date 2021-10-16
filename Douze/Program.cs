

using Douze;
using Douze.FileProcessors;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;

var stopwatch = new Stopwatch();
stopwatch.Start();

// Parse command line options

var options = Options.ParseOptions(args);

Console.WriteLine($"Parsed options in {stopwatch.ElapsedMilliseconds} ms");

// Configure files extensions mappings

options.ContentMappings[".md"] = new MarkdownFileProcessor();
options.ContentMappings[".html"] = new HtmlFileProcessor();
options.DataMappings[".yml"] = new YamlDataFileProcessor();
options.DataMappings[".json"] = new JsonDataFileProcessor();

// Build site
var result = await BuildSiteAsync(options);

Console.WriteLine($"Site generated at '{Path.GetFullPath(Path.Combine(options.SourcePath, options.OutputPath))}' in {stopwatch.ElapsedMilliseconds} ms");

return result;

async Task<int> BuildSiteAsync(Options options)
{
    var files = Directory.GetFiles(Path.GetFullPath(options.SourcePath), "*.*", SearchOption.AllDirectories).OrderBy(x => x).ToArray();

    var globalDataDirectoryName = Path.GetFullPath(Path.Combine(options.SourcePath, options.DataFolder));
    var sourceDirectoryName = Path.GetDirectoryName(Path.GetFullPath(options.SourcePath)) ?? "";

    // Process all data files and store the values per folder

    var dataByPath = new ConcurrentDictionary<string, JsonObject>();

    // Data files are computed in parallel by directory

    var filesByDirectory = files.GroupBy(x => Path.GetDirectoryName(x)).ToDictionary(x => x.Key ?? "", x => x.ToArray());

    await Parallel.ForEachAsync(filesByDirectory, async (entry, cancellationToken) =>
    {
        foreach (var file in entry.Value)
        {
            var directoryName = entry.Key;

            // Don't process data files from the root folder, use the _data folder instead
            if (directoryName == sourceDirectoryName)
            {
                continue;
            }

            if (!dataByPath.TryGetValue(directoryName, out var data))
            {
                dataByPath[directoryName] = data = new JsonObject();
            }

            var extension = file.Substring(file.LastIndexOf('.'));

            if (options.DataMappings.TryGetValue(extension, out var processor))
            {
                var stopWatch = new Stopwatch();

                stopWatch.Restart();
                await foreach (var result in processor.ProcessFileAsync(file, options, data))
                {
                    if (result.FilePath != null)
                    {
                        var filePath = Path.Combine(Path.GetFullPath(options.OutputPath), result.FilePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? "");

                        using (var stream = File.Create(filePath))
                        {
                            await result.SaveFileAsync(stream);
                        }
                    }

                    Console.WriteLine($"{result.Description} in {stopWatch.ElapsedMilliseconds} ms");
                }
            }
        }
    });

    if (!dataByPath.TryGetValue(globalDataDirectoryName, out var globalData))
    {
        globalData = new JsonObject();
    }

    dataByPath[sourceDirectoryName] = globalData;

    // Process all content files

    await Parallel.ForEachAsync(files, async (file, cancellationToken) =>
    {
        // Gather all data from each parent folder

        var data = JsonHelper.Clone(globalData)?.AsObject() ?? new JsonObject();

        var directoryName = Path.GetDirectoryName(Path.GetFullPath(file)) ?? "";
        var folderDataObjects = new List<JsonObject>();

        while (directoryName != null)
        {
            if (dataByPath.TryGetValue(directoryName, out var folderData))
            {
                folderDataObjects.Insert(0, folderData);
            }

            if (directoryName == sourceDirectoryName)
            {
                break;
            }

            directoryName = Directory.GetParent(directoryName)?.FullName;
        }

        foreach (var folderData in folderDataObjects)
        {
            JsonHelper.Merge(data, folderData);
        }

        var extension = file.Substring(file.LastIndexOf('.'));

        // Apply corresponding file processor

        if (options.ContentMappings.TryGetValue(extension, out var processor))
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();
            
            await foreach (var result in processor.ProcessFileAsync(file, options, data))
            {
                if (result.FilePath != null)
                {
                    var filePath = Path.Combine(Path.GetFullPath(options.OutputPath), result.FilePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? "");

                    using (var stream = File.Create(filePath))
                    {
                        await result.SaveFileAsync(stream);
                    }
                }
                Console.WriteLine($"{result.Description} in {stopwatch.ElapsedMilliseconds} ms");
            }
        }
    });

    return 0;
}
