using Douze.Markdown;
using Douze.Nodes;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: links</c>. Extracts the links and the anchors of HTML pages into their metadata, for <c>check-links</c>:
/// <c>links</c> (<c>{ url, line }</c> of every <c>href</c>, <c>src</c>, <c>srcset</c> candidate, <c>poster</c> and
/// <c>data-src</c>, the url as written, sorted by line then url, distinct) and <c>anchors</c> (the <c>id</c>s, and the
/// <c>name</c>s of <c>&lt;a&gt;</c>, sorted, distinct). The content is unchanged. Put it after <c>layout</c>, so that the
/// links of the layouts are checked too.
/// </summary>
internal sealed class LinksStep : Step, IMapStep
{
    public const string LinksKey = "links";
    public const string AnchorsKey = "anchors";

    private static readonly GlobSet Pages = GlobSet.Parse(["**/*.html"]);

    public LinksStep(StepDefinition definition) : base(definition)
    {
        _ = new WithOptions(definition);
    }

    public override StepShape NativeShape => StepShape.Map;

    public override GlobSet DefaultMatch => Pages;

    public override bool ReadsScopes => false;

    // A unit reads the content only: a locale copy whose content is its page's shares its result.
    public override bool RecordsOwnReads => true;

    public async ValueTask<FileList> ProcessAsync(VirtualFile file, StepContext context, CancellationToken ct)
    {
        if (!file.Content.IsText)
        {
            return FileList.Of(file);
        }

        var links = new List<HtmlLink>();
        var anchors = new List<string>();
        LinkScanner.Scan(await file.Content.ReadTextAsync(ct), links, anchors);

        var linkNodes = new NodeArray();

        foreach (var link in links.Distinct().Order(LinkOrder.Instance))
        {
            linkNodes.Add(new NodeObject { ["url"] = NodeValue.Create(link.Url), ["line"] = NodeValue.Create(link.Line) });
        }

        var anchorNodes = new NodeArray();

        foreach (var anchor in anchors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            anchorNodes.Add(NodeValue.Create(anchor));
        }

        return FileList.Of(file.With(Name, metadata: file.Metadata.With(own =>
        {
            own[LinksKey] = linkNodes;
            own[AnchorsKey] = anchorNodes;
        })));
    }

    private sealed class LinkOrder : IComparer<HtmlLink>
    {
        public static readonly LinkOrder Instance = new();

        public int Compare(HtmlLink x, HtmlLink y)
        {
            var line = x.Line.CompareTo(y.Line);
            return line != 0 ? line : string.CompareOrdinal(x.Url, y.Url);
        }
    }
}
