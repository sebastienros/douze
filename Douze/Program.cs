using System.Diagnostics;
using Douze;
using Douze.FileSystems;

var stopwatch = Stopwatch.StartNew();

// Parse command line options

var arguments = Arguments.ParseArguments(args);

var sourceFs = new PhysicalFileSystem(Path.GetFullPath(arguments.SourcePath));
var outputFs = new PhysicalFileSystem(Path.GetFullPath(arguments.OutputPath));

// if a content folder is provided, use it
// otherwise if "/_content/" exists use it
// otherwise use "/"
// same for _data and _includes

if (!arguments.ContentFolders.Any())
{
    arguments.ContentFolders.Add((await sourceFs.GetDirectoryEntryAsync(Options.DefaultContentFolder)).Exists
        ? Options.DefaultContentFolder.FullName
        : FPath.Root.FullName);
}

if (!arguments.DataFolders.Any())
{
    arguments.ContentFolders.Add((await sourceFs.GetDirectoryEntryAsync(Options.DefaultDataFolder)).Exists
        ? Options.DefaultDataFolder.FullName
        : FPath.Root.FullName);
}

if (!arguments.IncludesFolders.Any())
{
    arguments.ContentFolders.Add((await sourceFs.GetDirectoryEntryAsync(Options.DefaultIncludesFolder)).Exists
        ? Options.DefaultIncludesFolder.FullName
        : FPath.Root.FullName);
}

var options = new Options(
    sourceFs,
    outputFs,
    arguments.DataFolders,
    arguments.ContentFolders,
    arguments.IncludesFolders
    );

Console.WriteLine($"Parsed options in {stopwatch.ElapsedMilliseconds} ms");

// Configure files extensions mappings

options.BuildDefaultPipeline();

// Build site
var result = await SiteHelper.BuildSiteAsync(options);

if (result == 0)
{
    Console.WriteLine($"Site generated at '{Path.GetFullPath(arguments.OutputPath)}' in {stopwatch.ElapsedMilliseconds} ms");
}
else
{
    Console.WriteLine($"There were some errors while generating the site.");
}

return result;
