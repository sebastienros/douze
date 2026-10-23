using Douze.FileSystems;
using Douze.Markdown;
using Douze.Nodes;

namespace Douze.Workflow.Yaml;

/// <summary>
/// A loaded workflow file.
/// </summary>
/// <param name="Site">The <c>site</c> section; every value is null when it is absent.</param>
/// <param name="Markdown">The <c>markdown</c> section, or <c>null</c> when it is absent.</param>
/// <param name="Workflow">The <c>steps</c> section, or <c>null</c> when it is absent (the default workflow applies).</param>
/// <param name="Vars">The <c>vars</c> section, with the values set by the environment and the command line.</param>
internal sealed record WorkflowFile(SiteSettings Site, MarkdownOptions? Markdown, WorkflowDefinition? Workflow, NodeObject Vars);

/// <summary>
/// The <c>site</c> section of a workflow file. A null value is not set.
/// </summary>
internal sealed record SiteSettings
{
    public static readonly SiteSettings None = new();

    public FPath? ContentFolder { get; init; }
    public FPath? DataFolder { get; init; }
    public FPath? IncludesFolder { get; init; }
    public FPath? ShortcodesFolder { get; init; }
    public string? Output { get; init; }               // relative to the source folder, or absolute
    public string? Url { get; init; }
    public int? Parallelism { get; init; }
    public LocaleSet? Locales { get; init; }           // site.locales; SiteOptions resolves its root in the content folder
}
