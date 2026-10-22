using System.Globalization;
using Douze.FileSystems;
using Douze.Nodes;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: collect</c>. Builds <c>collections.all</c>, one <c>collections.&lt;tag&gt;</c> per tag and
/// <c>collections.urls</c>, emits no files, and contributes <c>collections</c> (or <c>with.name</c>) to the site scope.
/// Each item is <c>{ data: &lt;the file's metadata&gt; }</c>, plus <c>content</c> (the file's text) with
/// <c>with.content: true</c>. Items are sorted by date (<c>date</c>, else <c>page.date</c>), then by path.
/// <c>urls</c> maps the normalized <c>url</c> of every item (no trailing <c>index.html</c>) to the same item node as in
/// <c>all</c>; when two items have the same url, the first one in <c>all</c> wins.
/// </summary>
internal sealed class CollectStep : Step, IReduceStep
{
    public const string Collections = "collections";
    public const string All = "all";
    public const string Urls = "urls";

    private static readonly GlobSet Pages = GlobSet.Parse(["**/*.html", "**/*.md"]);

    private readonly string _name;
    private readonly bool _content;

    public CollectStep(StepDefinition definition) : base(definition)
    {
        var options = new WithOptions(definition, "name", "content");
        _name = options.GetString("name", Collections)!;
        _content = options.GetBool("content");

        if (_name.Length == 0)
        {
            throw new WorkflowValidationException($"option 'name' of step '{Name}' must not be empty", definition.Location);
        }
    }

    public override StepShape NativeShape => StepShape.Reduce;

    public override GlobSet DefaultMatch => Pages;

    // Collections hold the metadata of the pages, and their content only with 'content: true': otherwise editing a body
    // does not build them again.
    public override bool ReadsContent => _content;

    public async ValueTask<StepOutput> ProcessAllAsync(IReadOnlyList<VirtualFile> files, StepContext context, CancellationToken ct)
    {
        var scopes = context.Scopes;
        var deps = context.Dependencies;
        var items = new List<Item>(files.Count);
        var tagged = new SortedDictionary<string, List<Item>>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var node = new NodeObject { ["data"] = new NodeValue<Metadata>(file.Metadata) };

            if (_content)
            {
                node["content"] = file.Content.IsText ? NodeValue.Create(await file.Content.ReadTextAsync(ct)) : null;
            }

            var item = new Item(node, GetDate(file.Metadata, scopes, deps), file.Path);
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

        var all = Sorted(items);
        var collections = new NodeObject { [All] = all };

        foreach (var (tag, list) in tagged)
        {
            collections[tag] = Sorted(list);
        }

        collections[Urls] = ByUrl(all);

        var scope = new SiteScope($"step:{Name}", new NodeObject { [_name] = collections });

        return new StepOutput(FileList.None, scope);
    }

    /// <summary>
    /// The items by normalized url (<see cref="LocaleSet.NormalizeUrl"/>), in the order of <paramref name="all"/>.
    /// </summary>
    private static NodeObject ByUrl(NodeArray all)
    {
        var urls = new NodeObject();

        foreach (var item in all)
        {
            if (item is NodeObject obj
                && obj["data"] is NodeValue<Metadata> data
                && data.Value.Own["url"] is NodeValue<string> { Value: { Length: > 0 } url }
                && !urls.ContainsKey(LocaleSet.NormalizeUrl(url)))
            {
                urls[LocaleSet.NormalizeUrl(url)] = item;
            }
        }

        return urls;
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
