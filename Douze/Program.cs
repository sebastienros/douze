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

options.ContentProcessors.Add(new PageMetadataProcessor());
options.ContentProcessors.Add(new FrontMatterProcessor(static (FPath x) => Array.IndexOf(new[] { ".md", ".html", ".liquid" }, x.GetExtensionWithDot().ToLowerInvariant()) != -1));
options.ContentProcessors.Add(new MarkdownFileProcessor(static (FPath x) => Array.IndexOf(new[] { ".md" }, x.GetExtensionWithDot().ToLowerInvariant()) != -1));
options.ContentProcessors.Add(new HtmlFileProcessor(static (FPath x) => Array.IndexOf(new[] { ".html", ".liquid" }, x.GetExtensionWithDot().ToLowerInvariant()) != -1));
options.ContentProcessors.Add(new CopyDocumentProcessor(static (FPath x) => Array.IndexOf(new[] { ".jpg", ".gif", ".png" }, x.GetExtensionWithDot().ToLowerInvariant()) != -1));

options.DataProcessors.Add(new YamlDataFileProcessor());
options.DataProcessors.Add(new JsonDataFileProcessor());

// Build site
var result = await SiteHelper.BuildSiteAsync(options);

Console.WriteLine($"Site generated at '{Path.GetFullPath(arguments.OutputPath)}' in {stopwatch.ElapsedMilliseconds} ms");

return result;
