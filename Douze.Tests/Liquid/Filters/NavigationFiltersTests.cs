using Douze.Liquid.Filters;
using Douze.Nodes;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Liquid.Filters;

public class NavigationFiltersTests : FilterTests
{
    private static Node Sidebar() => Yaml("""
        - label: { en: Foundations, fr: Fondations }
          icon: book
          link: /docs/
          items:
            - label: Get started
              collapsed: true
              items:
                - { label: Install, link: /get-started/install/index.html, badge: { text: New } }
                - { label: First app, link: /get-started/first-app/ }
                - /get-started/deploy/
            - label: Concepts
              items:
                - { label: Model, link: /concepts/model/ }
        - label: Reference
          link: /reference/
          items:
            - { label: API, link: /reference/api/ }
            - { label: GitHub, link: "https://github.com/x/y", external: true }
        """)!;

    private static readonly LocaleSet Site = Locales("default: en\nlanguages: { ja: { lang: ja }, fr: {} }");

    private static ScopeSet Data(Node sidebar, LocaleSet? locales = null, NodeObject? urls = null)
    {
        var data = new NodeObject { ["sidebar"] = sidebar };

        if (urls != null)
        {
            data["collections"] = new NodeObject { ["urls"] = urls };
        }

        return Scopes(data, locales);
    }

    private static NodeObject Item(string title) => new() { ["data"] = new NodeValue<Metadata>(new Metadata(new NodeObject { ["title"] = NodeValue.Create(title) }, "/")) };

    private Task<string> RenderAsync(string template, ScopeSet scopes, LocaleSet? locales = null, DependencyTracker? deps = null, Metadata? page = null)
    {
        return RenderAsync(template, page ?? Page("/"), scopes, locales, deps);
    }

    [Fact]
    public async Task ActiveMarksTheItemAndOpensItsAncestors()
    {
        const string Template = """
            {%- assign nav = sidebar | nav_active: '/get-started/first-app/index.html' -%}
            {%- for topic in nav %}[{{ topic.open }}{% for g in topic.items %}({{ g.label }}:{{ g.open }}{% for i in g.items %} {{ i.label }}={{ i.active }}{% endfor %}){% endfor %}]{% endfor -%}
            """;

        var text = await RenderAsync(Template, Data(Sidebar()));

        Assert.Equal("[true(Get started:true Install= First app=true /get-started/deploy/=)(Concepts: Model=)][(API:)(GitHub:)]", text);
    }

    [Fact]
    public async Task InputIsNeverChanged()
    {
        var sidebar = Sidebar();
        var scopes = Data(sidebar);

        Assert.Equal("true", await RenderAsync("{% assign nav = sidebar | nav_active: '/concepts/model/' %}{{ nav[0].open }}", scopes));
        await RenderAsync("{{ sidebar | nav_breadcrumbs: '/concepts/model/' | size }}", scopes);

        // Hashes are cached on nodes: a copy is hashed anew.
        Assert.Equal(NodeHash.Of(Sidebar()), NodeHash.Of(sidebar.Clone()));
        Assert.Null(sidebar[0]!["open"]);
        Assert.Null(sidebar[0]!["items"]![1]!["items"]![0]!["active"]);
    }

    [Fact]
    public async Task UntouchedSubtreesAreTheInputsOwn()
    {
        var sidebar = Sidebar();
        var scopes = Data(sidebar);

        // The Reference topic has no bare url and is not on the trail: the same nodes are returned. Nodes compare by
        // value, so the identity is checked through a node that only the input has.
        var text = await RenderAsync("{% assign nav = sidebar | nav_active: '/concepts/model/' %}{{ nav[1].items[1].external }} {{ nav[1].open }}", scopes);
        Assert.Equal("true ", text);
    }

    [Fact]
    public async Task PrevAndNextStayInTheGroup()
    {
        var scopes = Data(Sidebar());

        Assert.Equal("Install /get-started/deploy/", await RenderAsync("{% assign n = sidebar | nav_prev_next: '/get-started/first-app/' %}{{ n.prev.label }} {{ n.next.link }}", scopes));
        Assert.Equal("|First app", await RenderAsync("{% assign n = sidebar | nav_prev_next: '/get-started/install/' %}{{ n.prev.label }}|{{ n.next.label }}", scopes));

        // The last of its group: no link to the first of the next group.
        Assert.Equal("First app|", await RenderAsync("{% assign n = sidebar | nav_prev_next: '/get-started/deploy/index.html' %}{{ n.prev.label }}|{{ n.next.label }}", scopes));
        Assert.Equal("|", await RenderAsync("{% assign n = sidebar | nav_prev_next: '/concepts/model/' %}{{ n.prev.label }}|{{ n.next.label }}", scopes));

        Assert.Equal("nil", await RenderAsync("{% assign n = sidebar | nav_prev_next: '/elsewhere/' %}{% if n == nil %}nil{% endif %}", scopes));
    }

    [Fact]
    public async Task Breadcrumbs()
    {
        var text = await RenderAsync("{% assign list = sidebar | nav_breadcrumbs: '/get-started/install/' %}{% for c in list %}{{ c | nav_label }} > {% endfor %}", Data(Sidebar()));
        Assert.Equal("Foundations > Get started > Install > ", text);

        Assert.Equal("0", await RenderAsync("{{ sidebar | nav_breadcrumbs: '/elsewhere/' | size }}", Data(Sidebar())));
    }

    [Fact]
    public async Task FlattenListsTheLeavesInOrder()
    {
        var text = await RenderAsync("{% assign list = sidebar | nav_flatten %}{% for l in list %}{{ l.link }} {% endfor %}", Data(Sidebar()));
        Assert.Equal("/get-started/install/index.html /get-started/first-app/ /get-started/deploy/ /concepts/model/ /reference/api/ https://github.com/x/y ", text);
    }

    [Fact]
    public async Task SectionIsTheTopic()
    {
        Assert.Equal("Reference", await RenderAsync("{% assign s = sidebar | nav_section: '/reference/api/' %}{{ s.label }}", Data(Sidebar())));
        Assert.Equal("book", await RenderAsync("{% assign s = sidebar | nav_section: '/docs/' %}{{ s.icon }}", Data(Sidebar())));
        Assert.Equal("", await RenderAsync("{% assign s = sidebar | nav_section: '/elsewhere/' %}{{ s.label }}", Data(Sidebar())));
    }

    [Fact]
    public async Task LabelsByLocaleWithFallback()
    {
        var scopes = Data(Sidebar(), Site);

        Assert.Equal("Fondations", await RenderAsync("{{ sidebar[0] | nav_label: 'fr' }}", scopes, Site));
        Assert.Equal("Foundations", await RenderAsync("{{ sidebar[0] | nav_label: 'ja' }}", scopes, Site));
        Assert.Equal("Fondations", await RenderAsync("{{ sidebar[0] | nav_label }}", scopes, Site, page: Page("/", ("locale", NodeValue.Create("fr")))));
        Assert.Equal("Reference", await RenderAsync("{{ sidebar[1] | nav_label: 'fr' }}", scopes, Site));
    }

    [Fact]
    public async Task LocalePrefixIsRemovedWhenTheLinksHaveNone()
    {
        var scopes = Data(Sidebar(), Site);

        Assert.Equal("Get started > Model", await RenderAsync("{% assign c = sidebar | nav_breadcrumbs: '/ja/concepts/model/index.html' %}{{ c[0].items[0].label }} > {{ c[2].label }}", scopes, Site));

        // Links with prefixes are matched as they are.
        var prefixed = Yaml("- { label: A, link: /ja/a/ }\n- { label: B, link: /b/ }")!;
        Assert.Equal("A|", await RenderAsync("{% assign s = sidebar | nav_section: '/ja/a/' %}{{ s.label }}|{% assign t = sidebar | nav_section: '/ja/b/' %}{{ t.label }}", Data(prefixed, Site), Site));
    }

    [Fact]
    public async Task BareUrlsAreLabeledFromCollectionsWithADependencyOnThatUrl()
    {
        var urls = new NodeObject { ["/get-started/deploy/"] = Item("Deploy"), ["/ja/get-started/deploy/"] = Item("デプロイ"), ["/other/"] = Item("Other") };
        var deps = new DependencyTracker();

        var text = await RenderAsync("{% assign list = sidebar | nav_flatten %}{% for l in list %}{{ l.label }},{% endfor %}", Data(Sidebar(), Site, urls), Site, deps);
        Assert.Contains("First app,Deploy,Model", text);

        var lookup = Assert.Single(deps.ToSet().Lookups, x => x.Key == "collections");
        var read = Assert.Single(lookup.Access!.Members);
        Assert.Equal("urls", read.Key);
        Assert.Equal(["/get-started/deploy/"], read.Access.Members.Select(x => x.Key));

        // A page of a locale shows the title of its own locale's page.
        var japanese = await RenderAsync("{% assign list = sidebar | nav_active: '/ja/docs/' %}{% for l in list %}{% for g in l.items %}{% for i in g.items %}{{ i.label }},{% endfor %}{% endfor %}{% endfor %}", Data(Sidebar(), Site, urls), Site);
        Assert.Contains("デプロイ", japanese);
    }

    [Fact]
    public async Task TheSidebarIsReadWhole()
    {
        var deps = new DependencyTracker();
        await RenderAsync("{% assign s = sidebar | nav_section: '/reference/api/' %}{{ s.label }}", Data(Sidebar()), deps: deps);

        var lookup = Assert.Single(deps.ToSet().Lookups, x => x.Key == "sidebar");
        Assert.Null(lookup.Access);
    }

    [Fact]
    public async Task IndexIsBuiltOncePerSidebar()
    {
        var sidebar = Sidebar();
        var scopes = Data(sidebar);
        Assert.Null(NavigationFilters.IndexOf(sidebar));

        await RenderAsync("{{ sidebar | nav_flatten | size }}", scopes);
        var index = NavigationFilters.IndexOf(sidebar);
        Assert.NotNull(index);

        await RenderAsync("{{ sidebar | nav_flatten | size }}{% assign n = sidebar | nav_prev_next: '/concepts/model/' %}", scopes);
        Assert.Same(index, NavigationFilters.IndexOf(sidebar));
    }
}
