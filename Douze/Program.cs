using Douze;
using Douze.Configuration;
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
options.ContentProcessors.Add(new FrontMatterProcessor(IncludeRule.ParseRules("*.md", "*.html", "*.liquid")));
options.ContentProcessors.Add(new MarkdownFileProcessor(IncludeRule.ParseRules("*.md")));
options.ContentProcessors.Add(new UrlFileProcessor(IncludeRule.ParseRules("*.md", "*.html")));
options.ContentProcessors.Add(new PaginationProcessor(IncludeRule.ParseRules("*.html")));
options.ContentProcessors.Add(new CollectionsProcessor(IncludeRule.ParseRules("*.html")));
options.ContentProcessors.Add(new LiquidTemplateFileProcessor(IncludeRule.ParseRules("*.html", "*.json")));
options.ContentProcessors.Add(new LayoutFileProcessor(IncludeRule.ParseRules("*.html")));
options.ContentProcessors.Add(new CopyDocumentProcessor(IncludeRule.ParseRules("*.jpg", "*.gif", "*.png")));

options.DataProcessors.Add(new YamlDataFileProcessor());
options.DataProcessors.Add(new JsonDataFileProcessor());

// Build site
var result = await SiteHelper.BuildSiteAsync(options);

Console.WriteLine($"Site generated at '{Path.GetFullPath(arguments.OutputPath)}' in {stopwatch.ElapsedMilliseconds} ms");

return result;
