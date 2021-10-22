using Douze;
using Douze.FileProcessors;
using Douze.FileSystems;
using System.Diagnostics;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Douze.Tests")]

var stopwatch = Stopwatch.StartNew();

// Parse command line options

var arguments = Arguments.ParseArguments(args);

var options = new Options(
    new PhysicalFileSystem(Path.GetFullPath(arguments.SourcePath)),
    new PhysicalFileSystem(Path.GetFullPath(arguments.OutputPath))
    );

Console.WriteLine($"Parsed options in {stopwatch.ElapsedMilliseconds} ms");

// Configure files extensions mappings

options.ContentMappings[".md"] = new MarkdownFileProcessor();
options.ContentMappings[".html"] = new HtmlFileProcessor();
options.ContentMappings[".liquid"] = new HtmlFileProcessor();
options.DataMappings[".yml"] = new YamlDataFileProcessor();
options.DataMappings[".json"] = new JsonDataFileProcessor();

// Build site
var result = await SiteHelper.BuildSiteAsync(options);

Console.WriteLine($"Site generated at '{Path.GetFullPath(arguments.OutputPath)}' in {stopwatch.ElapsedMilliseconds} ms");

return result;
