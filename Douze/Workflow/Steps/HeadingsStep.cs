using System.Collections.Immutable;
using Douze.Markdown;
using Douze.Nodes;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: headings</c>. Gives every heading of an HTML file an id, in one pass over the whole page so that the
/// headings of the page, of shortcodes and of includes share one set of ids: an existing <c>id</c> is kept, the others
/// are GitHub style slugs of their text (<see cref="Slugger"/>), <c>-1</c>, <c>-2</c>... for duplicates. Sets
/// <c>headings</c> (every heading: <c>depth</c>, <c>text</c>, <c>id</c>), <c>toc</c> (the headings of
/// <c>with.levels</c>, default <c>[2, 3]</c>, nested in <c>children</c>) and <c>page.heading</c> (the text of the first
/// <c>h1</c>). <c>with.anchors: true</c> appends <c>&lt;a class="heading-anchor" href="#id" aria-label="..."&gt;#&lt;/a&gt;</c>
/// to each heading (<c>with.anchor-class</c>, <c>with.anchor-label</c>).
/// </summary>
internal sealed class HeadingsStep : Step, IMapStep
{
    private static readonly GlobSet HtmlFiles = GlobSet.Parse(["**/*.html"]);

    private readonly bool _anchors;
    private readonly string _anchorClass;
    private readonly string _anchorLabel;
    private readonly ImmutableArray<int> _levels;

    public HeadingsStep(StepDefinition definition) : base(definition)
    {
        var options = new WithOptions(definition, "anchors", "anchor-class", "anchor-label", "levels");

        _anchors = options.GetBool("anchors");
        _anchorClass = options.GetString("anchor-class", "heading-anchor")!;
        _anchorLabel = options.GetString("anchor-label", "Link to this heading")!;
        _levels = ReadLevels(definition);
    }

    public override StepShape NativeShape => StepShape.Map;

    public override GlobSet DefaultMatch => HtmlFiles;

    public async ValueTask<FileList> ProcessAsync(VirtualFile file, StepContext context, CancellationToken ct)
    {
        var html = await file.Content.ReadTextAsync(ct);
        var headings = HeadingScanner.Scan(html);

        if (headings.Count == 0)
        {
            return FileList.Of(file);
        }

        // Ids the page sets itself are reserved first, so that a generated one never takes them.
        var scope = new Slugger.Scope();

        foreach (var heading in headings)
        {
            if (heading.Id != null)
            {
                scope.Reserve(heading.Id);
            }
        }

        var ids = headings.Select(x => x.Id ?? scope.Unique(Slugger.Slug(x.Text))).ToArray();
        var index = headings.Select((x, i) => (x.Start, i)).ToDictionary(x => x.Start, x => x.i);

        var label = HeadingScanner.EncodeAttribute(_anchorLabel);
        var anchorClass = HeadingScanner.EncodeAttribute(_anchorClass);

        var rewritten = HeadingScanner.Rewrite(
            html,
            headings,
            x => ids[index[x.Start]],
            x => _anchors ? $"<a class=\"{anchorClass}\" href=\"#{HeadingScanner.EncodeAttribute(ids[index[x.Start]])}\" aria-label=\"{label}\">#</a>" : null);

        var all = new NodeArray();

        for (var i = 0; i < headings.Count; i++)
        {
            all.Add(new NodeObject
            {
                ["depth"] = NodeValue.Create(headings[i].Depth),
                ["text"] = NodeValue.Create(headings[i].Text),
                ["id"] = NodeValue.Create(ids[i]),
            });
        }

        var toc = Toc(headings, ids);
        var title = headings.FirstOrDefault(x => x.Depth == 1).Text;

        var metadata = file.Metadata.With(own =>
        {
            own["headings"] = all;
            own["toc"] = toc;

            if (title != null)
            {
                // page is shared with other files: replaced, not changed.
                var page = own["page"] is NodeObject existing ? existing.ShallowClone() : new NodeObject();
                page["heading"] = NodeValue.Create(title);
                own["page"] = page;
            }
        });

        return FileList.Of(file.With(Name, content: FileContent.FromText(rewritten), metadata: metadata));
    }

    /// <summary>
    /// The headings of <see cref="_levels"/> as a tree: a heading is a child of the closest previous one of a lower
    /// depth.
    /// </summary>
    private NodeArray Toc(List<Heading> headings, string[] ids)
    {
        var root = new List<Entry>();
        var stack = new Stack<Entry>();

        for (var i = 0; i < headings.Count; i++)
        {
            var heading = headings[i];

            if (!_levels.Contains(heading.Depth))
            {
                continue;
            }

            while (stack.Count > 0 && stack.Peek().Depth >= heading.Depth)
            {
                stack.Pop();
            }

            var entry = new Entry(heading.Depth, heading.Text, ids[i]);
            (stack.Count > 0 ? stack.Peek().Children : root).Add(entry);
            stack.Push(entry);
        }

        return ToNodes(root);

        static NodeArray ToNodes(List<Entry> entries)
        {
            var array = new NodeArray();

            foreach (var entry in entries)
            {
                array.Add(new NodeObject
                {
                    ["depth"] = NodeValue.Create(entry.Depth),
                    ["text"] = NodeValue.Create(entry.Text),
                    ["id"] = NodeValue.Create(entry.Id),
                    ["children"] = ToNodes(entry.Children),
                });
            }

            return array;
        }
    }

    private ImmutableArray<int> ReadLevels(StepDefinition definition)
    {
        if (definition.With["levels"] is not { } levels)
        {
            return [2, 3];
        }

        var items = levels is NodeArray array ? array.ToList() : [levels];
        var result = new List<int>();

        foreach (var item in items)
        {
            int? level = item switch
            {
                NodeValue<int> i => i.Value,
                NodeValue<long> l => (int)Math.Clamp(l.Value, 0, 7),
                NodeValue<decimal> m when m.Value == decimal.Truncate(m.Value) => (int)Math.Clamp(m.Value, 0, 7),
                NodeValue<string> s when int.TryParse(s.Value, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => null,
            };

            if (level is not (>= 1 and <= 6))
            {
                throw new WorkflowValidationException($"option 'levels' of step '{Name}' must be a list of heading levels from 1 to 6", definition.Location);
            }

            result.Add(level.Value);
        }

        return [.. result.Distinct().Order()];
    }

    private sealed record Entry(int Depth, string Text, string Id)
    {
        public List<Entry> Children { get; } = [];
    }
}
