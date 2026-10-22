using System.Runtime.CompilerServices;
using Douze.Nodes;
using Douze.Workflow;
using Fluid;
using Fluid.Values;

namespace Douze.Liquid.Filters;

/// <summary>
/// Filters over a sidebar: a list of items, each a bare url or an object with <c>label</c> (a string, or an object of
/// labels by locale), <c>link</c>, <c>items</c> (a group) and anything else a template reads (<c>icon</c>,
/// <c>badge</c>, <c>collapsed</c>, <c>external</c>). Links and page urls are compared normalized (no trailing
/// <c>index.html</c>), and when no link of the sidebar has a locale prefix, the page url's prefix is removed first.
/// </summary>
/// <remarks>
/// The filters read the sidebar whole: a page that shows a sidebar depends on all of it. A sidebar node is indexed once
/// (its leaves in order, its links, the parent of each item), since published nodes never change; the input nodes are
/// never changed, results are new nodes or the input's own. The label of a bare url is the <c>title</c> of the page at
/// that url in <c>collections.urls</c> (the page of the same locale first), which records a dependency on that entry
/// only, or the url itself.
/// </remarks>
internal static class NavigationFilters
{
    private static readonly ConditionalWeakTable<Node, SidebarIndex> Indexes = new();

    public static void Register(TemplateOptionsBuilder builder)
    {
        builder
            .AddFilter("nav_active", Active)
            .AddFilter("nav_prev_next", PrevNext)
            .AddFilter("nav_breadcrumbs", Breadcrumbs)
            .AddFilter("nav_flatten", Flatten)
            .AddFilter("nav_section", Section)
            .AddFilter("nav_label", Label);
    }

    /// <summary>
    /// The index built for a sidebar node, if any (tests).
    /// </summary>
    internal static object? IndexOf(Node root) => Indexes.TryGetValue(root, out var index) ? index : null;

    /// <summary>
    /// <c>{{ sidebar | nav_active: url }}</c>: the sidebar with <c>active: true</c> on the item of the url and
    /// <c>open: true</c> on its ancestors, bare urls as <c>{ label, link }</c>.
    /// </summary>
    public static async ValueTask<FluidValue> Active(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        if (await ReadAsync(input, arguments.At(0), context) is not { } nav)
        {
            return input;
        }

        var active = nav.Find();
        var trail = new HashSet<SidebarIndex.Entry>();

        for (var e = active; e != null; e = e.Parent)
        {
            trail.Add(e);
        }

        var result = new NodeArray();

        foreach (var root in nav.Index.Roots)
        {
            result.Add(await TransformAsync(root, nav, active, trail, context));
        }

        return NodeValues.ToFluidValue(result);
    }

    private static async ValueTask<Node?> TransformAsync(SidebarIndex.Entry entry, Nav nav, SidebarIndex.Entry? active, HashSet<SidebarIndex.Entry> trail, TemplateContext context)
    {
        // A subtree with nothing to change is the input's own.
        if (!trail.Contains(entry) && !entry.HasBareUrls)
        {
            return entry.Node;
        }

        if (entry.Node is not NodeObject obj)
        {
            var bare = await nav.ItemAsync(entry, context);

            if (entry == active)
            {
                bare["active"] = NodeValue.Create(true);
            }

            return bare;
        }

        var clone = obj.ShallowClone();

        if (entry == active)
        {
            clone["active"] = NodeValue.Create(true);
        }
        else if (trail.Contains(entry))
        {
            clone["open"] = NodeValue.Create(true);
        }

        if (entry.Children.Length > 0)
        {
            var items = new NodeArray();

            foreach (var child in entry.Children)
            {
                items.Add(await TransformAsync(child, nav, active, trail, context));
            }

            clone["items"] = items;
        }

        return clone;
    }

    /// <summary>
    /// <c>{{ sidebar | nav_prev_next: url }}</c>: <c>{ prev, next }</c>, the leaves next to the url's item in the same
    /// group (none across groups, none at the ends); nil when the url is not in the sidebar.
    /// </summary>
    public static async ValueTask<FluidValue> PrevNext(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        if (await ReadAsync(input, arguments.At(0), context) is not { } nav || nav.Find() is not { } active)
        {
            return NilValue.Instance;
        }

        var leaves = nav.Index.Leaves;
        var i = active.LeafIndex;
        Node? prev = null, next = null;

        if (i >= 0)
        {
            if (i > 0 && leaves[i - 1].Parent == active.Parent)
            {
                prev = await nav.NodeAsync(leaves[i - 1], context);
            }

            if (i < leaves.Length - 1 && leaves[i + 1].Parent == active.Parent)
            {
                next = await nav.NodeAsync(leaves[i + 1], context);
            }
        }

        return NodeValues.ToFluidValue(new NodeObject { ["prev"] = prev, ["next"] = next });
    }

    /// <summary>
    /// <c>{{ sidebar | nav_breadcrumbs: url }}</c>: the items from the top of the sidebar to the url's item; empty when
    /// the url is not in the sidebar.
    /// </summary>
    public static async ValueTask<FluidValue> Breadcrumbs(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        if (await ReadAsync(input, arguments.At(0), context) is not { } nav)
        {
            return NodeValues.ToFluidValue(new NodeArray());
        }

        var chain = new List<SidebarIndex.Entry>();

        for (var e = nav.Find(); e != null; e = e.Parent)
        {
            chain.Add(e);
        }

        chain.Reverse();
        var result = new NodeArray();

        foreach (var entry in chain)
        {
            result.Add(await nav.NodeAsync(entry, context));
        }

        return NodeValues.ToFluidValue(result);
    }

    /// <summary>
    /// <c>{{ sidebar | nav_flatten }}</c>: the leaves of the sidebar in order (items with a link and no items).
    /// </summary>
    public static async ValueTask<FluidValue> Flatten(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        if (await ReadAsync(input, NilValue.Instance, context) is not { } nav)
        {
            return NodeValues.ToFluidValue(new NodeArray());
        }

        var result = new NodeArray();

        foreach (var leaf in nav.Index.Leaves)
        {
            result.Add(await nav.NodeAsync(leaf, context));
        }

        return NodeValues.ToFluidValue(result);
    }

    /// <summary>
    /// <c>{{ sidebar | nav_section: url }}</c>: the top-level item that contains the url's item, or nil.
    /// </summary>
    public static async ValueTask<FluidValue> Section(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        if (await ReadAsync(input, arguments.At(0), context) is not { } nav || nav.Find() is not { } active)
        {
            return NilValue.Instance;
        }

        var top = active;

        while (top.Parent != null)
        {
            top = top.Parent;
        }

        return NodeValues.ToFluidValue(await nav.NodeAsync(top, context));
    }

    /// <summary>
    /// <c>{{ item | nav_label: locale }}</c>: the label of an item in a locale (default: the page's <c>locale</c>): a
    /// string label, or the locale's member of an object of labels, else the default locale's, else its first one. A
    /// bare url is its own label.
    /// </summary>
    public static async ValueTask<FluidValue> Label(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        if (input.Type == FluidValues.String)
        {
            return input;
        }

        var label = await input.GetValueAsync("label", context);

        if (label.Type != FluidValues.Dictionary)
        {
            return label;
        }

        var locales = await FilterHelpers.GetLocalesAsync(context);
        var locale = FilterHelpers.AsString(arguments.At(0), context)
            ?? FilterHelpers.AsString(await FilterHelpers.ResolveAsync(context, "locale"), context)
            ?? locales.Default.Key;

        var value = await label.GetValueAsync(locale, context);

        if (value.IsNil() && locales.IsEnabled)
        {
            value = await label.GetValueAsync(locales.Default.Key, context);
        }

        if (value.IsNil())
        {
            await foreach (var pair in label.EnumerateAsync(context))
            {
                return await pair.GetIndexAsync(NumberValue.Create(1), context);
            }
        }

        return value;
    }

    /// <summary>
    /// Reads the sidebar whole and normalizes the url.
    /// </summary>
    private static async ValueTask<Nav?> ReadAsync(FluidValue input, FluidValue url, TemplateContext context)
    {
        Node? root = input switch
        {
            NodeArrayValue array => array.ReadAll(),
            NodeObjectValue obj => obj.ReadAll(),
            _ => null,
        };

        if (root == null)
        {
            return null;
        }

        var index = Indexes.GetValue(root, static x => new SidebarIndex(x));

        var locales = await FilterHelpers.GetLocalesAsync(context);
        var prefix = "";
        string? normalized = null;

        if (FilterHelpers.AsString(url, context) is { } text)
        {
            normalized = LocaleSet.NormalizeUrl(text);

            if (locales.IsEnabled && !index.HasLocalePrefixes(locales))
            {
                var locale = locales.LocaleOfUrl(normalized);

                if (!locale.IsDefault)
                {
                    prefix = locale.Prefix;
                    normalized = normalized[prefix.Length..];
                }
            }
        }

        return new Nav(index, normalized, prefix);
    }

    /// <summary>
    /// A sidebar, the page url to find in it and the locale prefix removed from that url.
    /// </summary>
    private sealed record Nav(SidebarIndex Index, string? Url, string Prefix)
    {
        public SidebarIndex.Entry? Find() => Url != null && Index.ByUrl.TryGetValue(Url, out var entry) ? entry : null;

        /// <summary>
        /// The node of an item as the input has it, or <c>{ label, link }</c> for a bare url.
        /// </summary>
        public async ValueTask<Node?> NodeAsync(SidebarIndex.Entry entry, TemplateContext context)
        {
            return entry.Node is NodeObject ? entry.Node : await ItemAsync(entry, context);
        }

        /// <summary>
        /// A new <c>{ label, link }</c> for a bare url, labeled with the title of its page.
        /// </summary>
        public async ValueTask<NodeObject> ItemAsync(SidebarIndex.Entry entry, TemplateContext context)
        {
            var link = entry.RawLink ?? "";
            var title = await TitleAsync(Prefix + entry.Link, context);

            if (title == null && Prefix.Length > 0)
            {
                title = await TitleAsync(entry.Link!, context);
            }

            return new NodeObject { ["label"] = NodeValue.Create(title ?? link), ["link"] = NodeValue.Create(link) };
        }

        private static async ValueTask<string?> TitleAsync(string url, TemplateContext context)
        {
            var collections = await FilterHelpers.ResolveAsync(context, "collections");

            if (collections.IsNil())
            {
                return null;
            }

            var urls = await collections.GetValueAsync("urls", context);
            var item = urls.IsNil() ? urls : await urls.GetValueAsync(url, context);
            return item.IsNil() ? null : FilterHelpers.AsString(await FilterHelpers.GetPathAsync(item, "data.title", context), context);
        }
    }

    /// <summary>
    /// The items of a sidebar in document order, with their parents, the leaves in order and the items by link.
    /// </summary>
    private sealed class SidebarIndex
    {
        // The answer of HasLocalePrefixes for the last locales asked about; a reference, replaced as a whole.
        private Prefixes? _prefixes;

        public SidebarIndex(Node root)
        {
            var roots = new List<Entry>();
            var leaves = new List<Entry>();
            var byUrl = new Dictionary<string, Entry>(StringComparer.Ordinal);

            IEnumerable<Node?> items = root is NodeArray array ? array : [root];

            foreach (var item in items)
            {
                if (Build(item, null) is { } entry)
                {
                    roots.Add(entry);
                }
            }

            Roots = [.. roots];
            Leaves = [.. leaves];
            ByUrl = byUrl;

            Entry? Build(Node? node, Entry? parent)
            {
                string? link;
                NodeArray? children = null;

                switch (node)
                {
                    case NodeValue<string> s:
                        link = s.Value;
                        break;
                    case NodeObject obj:
                        link = obj["link"] is NodeValue<string> l ? l.Value : null;
                        children = obj["items"] as NodeArray;
                        break;
                    default:
                        return null;
                }

                var entry = new Entry(node, parent, link);

                if (entry.Link != null)
                {
                    byUrl.TryAdd(entry.Link, entry);
                }

                if (children != null)
                {
                    var built = new List<Entry>(children.Count);

                    foreach (var child in children)
                    {
                        if (Build(child, entry) is { } c)
                        {
                            built.Add(c);
                        }
                    }

                    entry.Children = [.. built];
                    entry.HasBareUrls = built.Any(x => x.HasBareUrls);
                }
                else if (link != null)
                {
                    entry.LeafIndex = leaves.Count;
                    leaves.Add(entry);
                }

                entry.HasBareUrls |= node is NodeValue<string>;
                return entry;
            }
        }

        public Entry[] Roots { get; }

        public Entry[] Leaves { get; }

        public IReadOnlyDictionary<string, Entry> ByUrl { get; }

        /// <summary>
        /// Whether a link of the sidebar starts with the prefix of a locale (<c>/ja/</c>).
        /// </summary>
        public bool HasLocalePrefixes(LocaleSet locales)
        {
            if (Volatile.Read(ref _prefixes) is { } known && ReferenceEquals(known.Locales, locales))
            {
                return known.Prefixed;
            }

            var prefixed = ByUrl.Keys.Any(url => locales.Languages.Any(l => LocaleSet.HasPrefix(url, l.Prefix)));
            Volatile.Write(ref _prefixes, new Prefixes(locales, prefixed));
            return prefixed;
        }

        private sealed record Prefixes(LocaleSet Locales, bool Prefixed);

        public sealed class Entry(Node? node, Entry? parent, string? link)
        {
            public Node? Node { get; } = node;

            public Entry? Parent { get; } = parent;

            /// <summary>
            /// The link as written.
            /// </summary>
            public string? RawLink { get; } = link;

            /// <summary>
            /// The link, normalized.
            /// </summary>
            public string? Link { get; } = link == null ? null : LocaleSet.NormalizeUrl(link);

            public Entry[] Children { get; set; } = [];

            public int LeafIndex { get; set; } = -1;

            public bool HasBareUrls { get; set; }
        }
    }
}
