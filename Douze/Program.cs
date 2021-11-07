using System.Diagnostics;
using Douze;
using Douze.Configuration;
using Douze.FileProcessors;
using Douze.FileSystems;

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

options.ContentProcessors.Add(new MarkdownFileProcessor(IncludeRule.ParseRules("*.md")));
options.ContentProcessors.Add(new UrlFileProcessor(IncludeRule.ParseRules("*.md", "*.html")));
options.ContentProcessors.Add(new PaginationProcessor(IncludeRule.ParseRules("*.html")));
options.ContentProcessors.Add(new CollectionsProcessor(IncludeRule.ParseRules("*.html")));
options.ContentProcessors.Add(new LiquidTemplateFileProcessor(IncludeRule.ParseRules("*.html", "*.json")));
options.ContentProcessors.Add(new LayoutFileProcessor(IncludeRule.ParseRules("*.html")));
options.ContentProcessors.Add(new CopyDocumentProcessor(IncludeRule.ParseRules("*.jpg", "*.gif", "*.png")));

options.DataProcessors.Add(new YamlDataFileProcessor());
options.DataProcessors.Add(new JsonDataFileProcessor());
options.DataProcessors.Add(new PageMetadataProcessor());
options.DataProcessors.Add(new FrontMatterProcessor(IncludeRule.ParseRules("*.md", "*.html", "*.liquid")));

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
