using Douze;
using Douze.FileProcessors;
using System.Diagnostics;
using Zio.FileSystems;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Douze.Tests")]

var stopwatch = Stopwatch.StartNew();

// Parse command line options

var arguments = Arguments.ParseArguments(args);

var fs = new PhysicalFileSystem();

var options = new Options(
    new ReadOnlyFileSystem(new SubFileSystem(fs, fs.ConvertPathFromInternal(arguments.SourcePath))),
    new SubFileSystem(fs, fs.ConvertPathFromInternal(arguments.OutputPath))
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
