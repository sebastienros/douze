using System.Globalization;
using Douze.FileSystems;
using Douze.Nodes;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: collect</c>. Builds <c>collections.all</c> and one <c>collections.&lt;tag&gt;</c> per tag, emits no files,
/// and contributes <c>collections</c> to the site scope. Each item is <c>{ data: &lt;the file's metadata&gt; }</c>. Items
/// are sorted by date (<c>date</c>, else <c>page.date</c>), then by path.
/// </summary>
internal sealed class CollectStep : Step, IReduceStep
{
    public const string Collections = "collections";
    public const string All = "all";

    private static readonly GlobSet Pages = GlobSet.Parse(["**/*.html", "**/*.md"]);

    public CollectStep(StepDefinition definition) : base(definition)
    {
        _ = new WithOptions(definition);
    }

    public override StepShape NativeShape => StepShape.Reduce;

    public override GlobSet DefaultMatch => Pages;

    public ValueTask<StepOutput> ProcessAllAsync(IReadOnlyList<VirtualFile> files, StepContext context, CancellationToken ct)
    {
        var scopes = context.Scopes;
        var deps = context.Dependencies;
        var items = new List<Item>(files.Count);
        var tagged = new SortedDictionary<string, List<Item>>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var item = new Item(new NodeObject { ["data"] = new NodeValue<Metadata>(file.Metadata) }, GetDate(file.Metadata, scopes, deps), file.Path);
            items.Add(item);

            foreach (var tag in GetTags(file.Metadata, scopes, deps))
            {
                if (!tagged.TryGetValue(tag, out var list))
                {
                    tagged[tag] = list = [];
                }

                // A tag listed twice adds the file once.
                if (list.Count == 0 || !ReferenceEquals(list[^1], item))
                {
                    list.Add(item);
                }
            }
        }

        var collections = new NodeObject { [All] = Sorted(items) };

        foreach (var (tag, list) in tagged)
        {
            collections[tag] = Sorted(list);
        }

        var scope = new SiteScope($"step:{Name}", new NodeObject { [Collections] = collections });

        return new(new StepOutput(FileList.None, scope));
    }

    private static NodeArray Sorted(List<Item> items)
    {
        return new NodeArray(items.OrderBy(x => x.Date).ThenBy(x => x.Path).Select(x => (Node?)x.Node).ToArray());
    }

    private static IEnumerable<string> GetTags(Metadata metadata, ScopeSet scopes, DependencyTracker deps)
    {
        if (!scopes.TryResolve(metadata, "tags", null, deps, out var node, out _))
        {
            return [];
        }

        IEnumerable<Node?> tags = node switch
        {
            NodeArray array => array,
            NodeValue value => [value],
            _ => [],
        };

        return tags
            .Select(x => x is NodeValue<string> s ? s.Value : (x as IFormattable)?.ToString(null, CultureInfo.InvariantCulture))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!);
    }

    private static DateTimeOffset GetDate(Metadata metadata, ScopeSet scopes, DependencyTracker deps)
    {
        if (scopes.TryResolve(metadata, "date", null, deps, out var date, out _) && TryGetDate(date, out var result))
        {
            return result;
        }

        if (scopes.TryResolve(metadata, "page", null, deps, out var page, out _) && page is NodeObject pageObject && TryGetDate(pageObject["date"], out result))
        {
            return result;
        }

        return DateTimeOffset.MinValue;
    }

    private static bool TryGetDate(Node? node, out DateTimeOffset date)
    {
        switch (node)
        {
            case NodeValue<DateTimeOffset> dto:
                date = dto.Value;
                return true;

            case NodeValue<DateTime> dt:
                date = dt.Value.Kind == DateTimeKind.Unspecified ? new DateTimeOffset(dt.Value, TimeSpan.Zero) : new DateTimeOffset(dt.Value);
                return true;

            case NodeValue<string> s when DateTimeOffset.TryParse(s.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date):
                return true;
        }

        date = default;
        return false;
    }

    private sealed record Item(NodeObject Node, DateTimeOffset Date, FPath Path);
}
