using System.Collections.Immutable;
using Douze.FileSystems;
using Douze.Nodes;

namespace Douze.Workflow;

/// <summary>
/// The workflow of a site without a workflow file.
/// </summary>
internal static class DefaultWorkflow
{
    private const string DataExtensions = "{yml,yaml,json,xml}";

    /// <summary>
    /// Data files of the data folder are site data, data files of the content folder are folder data, and every other
    /// file of the content folder goes through front matter, permalinks, collections, pagination, Liquid, Markdown and
    /// layouts, then is written. Files no step claims (images, styles) are written as they are.
    /// </summary>
    public static WorkflowDefinition Create(SiteOptions site)
    {
        var content = Glob(site.ContentFolder);
        var data = Glob(site.DataFolder);

        var steps = ImmutableArray.CreateBuilder<StepDefinition>();

        // Without a data folder there is no site data: data files at the root are the folder data of the root, which
        // applies to every file.
        if (site.DataFolder != FPath.Root)
        {
            steps.Add(new StepDefinition
            {
                Name = "site-data",
                Inputs = [$"{data}**/*.{DataExtensions}"],
                Uses = "data",
                With = new NodeObject { ["scope"] = NodeValue.Create("site") },
            });
        }

        var folderData = new NodeObject { ["scope"] = NodeValue.Create("folder") };

        if (site.ContentFolder != FPath.Root)
        {
            folderData["root"] = NodeValue.Create(content.TrimEnd('/'));
        }

        steps.Add(new StepDefinition
        {
            Name = "folder-data",
            Inputs = [.. WithExclusions(site, [$"{content}**/*.{DataExtensions}"])],
            Uses = "data",
            With = folderData,
        });

        steps.Add(new StepDefinition
        {
            Name = "pages",
            Inputs = [.. WithExclusions(site, [$"{content}**", $"!{content}**/*.{DataExtensions}"])],
            Needs = site.DataFolder != FPath.Root ? ["site-data", "folder-data"] : ["folder-data"],
            Uses = "frontmatter",
        });

        // Collections are built before pagination, so a page can paginate over collections.<tag>; Liquid runs before
        // Markdown, as in Eleventy, so Markdown renders what Liquid produced.
        steps.Add(new StepDefinition { Name = "permalink", Needs = ["pages"], Uses = "permalink", With = Root(content) });
        steps.Add(new StepDefinition { Name = "collections", Needs = ["permalink"], Uses = "collect" });
        steps.Add(new StepDefinition { Name = "paginate", Needs = ["permalink", "collections"], Uses = "paginate" });
        steps.Add(new StepDefinition { Name = "render", Needs = ["paginate"], Uses = "liquid" });
        steps.Add(new StepDefinition { Name = "markdown", Needs = ["render"], Uses = "markdown" });
        steps.Add(new StepDefinition { Name = "layout", Needs = ["markdown"], Uses = "layout" });
        steps.Add(new StepDefinition { Name = "write", Needs = ["layout"], Uses = "write" });

        return new WorkflowDefinition(steps.ToImmutable(), SourceFile: null);
    }

    /// <summary>
    /// When the content folder is the root, it also holds the data and includes folders, the workflow file and maybe
    /// the output folder: their files are neither pages nor folder data.
    /// </summary>
    private static IEnumerable<string> WithExclusions(SiteOptions site, string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            yield return pattern;
        }

        if (site.ContentFolder != FPath.Root)
        {
            yield break;
        }

        if (site.DataFolder != FPath.Root)
        {
            yield return $"!{Glob(site.DataFolder)}**";
        }

        if (site.IncludesFolder != FPath.Root)
        {
            yield return $"!{Glob(site.IncludesFolder)}**";
        }

        yield return "!douze.yml";

        if (OutputFolder(site) is { } output)
        {
            yield return $"!{output}**";
        }
    }

    private static NodeObject Root(string content)
    {
        return content.Length == 0 ? new NodeObject() : new NodeObject { ["root"] = NodeValue.Create(content.TrimEnd('/')) };
    }

    /// <summary>
    /// The glob prefix of a folder: <c>_content/</c>, or empty for the root.
    /// </summary>
    private static string Glob(FPath folder) => folder == FPath.Root ? "" : folder.ToRelative().FullName;

    /// <summary>
    /// The output folder relative to the source folder (<c>_site/</c>), when it is inside it.
    /// </summary>
    private static string? OutputFolder(SiteOptions site)
    {
        if (site.SourceDirectory is not { } source || site.OutputDirectory is not { } output)
        {
            return null;
        }

        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)) + Path.DirectorySeparatorChar;
        var outputRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(output)) + Path.DirectorySeparatorChar;

        if (!outputRoot.StartsWith(sourceRoot, StringComparison.Ordinal) || outputRoot.Length == sourceRoot.Length)
        {
            return null;
        }

        return outputRoot[sourceRoot.Length..].Replace(Path.DirectorySeparatorChar, '/');
    }
}
