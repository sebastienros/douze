using Douze.FileSystems;
using Douze.Nodes;
using Douze.Progress;
using Douze.Tests.Workflow.Cache;
using Douze.Workflow;
using Douze.Workflow.Yaml;
using Xunit;

namespace Douze.Tests.Liquid.Filters;

public class LocaleFiltersTests : FilterTests
{
    private static readonly LocaleSet Site = Locales("""
        default: en
        root: docs
        fallback: true
        languages:
          ja: { label: 日本語, lang: ja }
          fr: { lang: fr-FR }
        """);

    private static ScopeSet Strings() => Scopes(new NodeObject
    {
        ["i18n"] = Yaml("""
            en:
              search: { label: Search, placeholder: Search the docs }
              pages.count: "{count} pages in {section}"
              only: English only
            ja:
              search: { label: 検索 }
            """),
    }, Site);

    private static Metadata JapanesePage() => Page("/docs/ja/", ("locale", NodeValue.Create("ja")));

    [Fact]
    public async Task TranslatesWithTheDefaultLocaleAsFallback()
    {
        var scopes = Strings();

        Assert.Equal("検索", await RenderAsync("{{ 'search.label' | t }}", JapanesePage(), scopes, Site));
        Assert.Equal("Search the docs", await RenderAsync("{{ 'search.placeholder' | t }}", JapanesePage(), scopes, Site));
        Assert.Equal("English only", await RenderAsync("{{ 'only' | t }}", JapanesePage(), scopes, Site));

        // A page without a locale is in the default locale.
        Assert.Equal("Search", await RenderAsync("{{ 'search.label' | t }}", Page("/docs/"), scopes, Site));
    }

    [Fact]
    public async Task NamedArgumentsReplacePlaceholders()
    {
        Assert.Equal("3 pages in Guides", await RenderAsync("{{ 'pages.count' | t: count: 3, section: 'Guides' }}", Page("/docs/"), Strings(), Site));
    }

    [Fact]
    public async Task TranslationDependsOnTheStringsItRead()
    {
        var deps = new DependencyTracker();
        await RenderAsync("{{ 'search.label' | t }}", JapanesePage(), Strings(), Site, deps);

        var lookup = Assert.Single(deps.ToSet().Lookups, x => x.Key == "i18n");
        var ja = Assert.Single(lookup.Access!.Members);
        Assert.Equal("ja", ja.Key);
        Assert.Equal(["search"], ja.Access.Members.Select(x => x.Key).Where(x => x == "search"));

        // The settings of the locales are a dependency too.
        Assert.Contains(deps.ToSet().Lookups, x => x.Key == "site");
    }

    [Fact]
    public async Task LocaleUrlAndLocaleOf()
    {
        Assert.Equal("/ja/guide/", await RenderAsync("{{ '/guide/index.html' | locale_url: 'ja' }}", locales: Site));
        Assert.Equal("/guide/", await RenderAsync("{{ '/ja/guide/' | locale_url: 'en' }}", locales: Site));
        Assert.Equal("/fr/", await RenderAsync("{{ '/ja/index.html' | locale_url: 'fr' }}", locales: Site));
        Assert.Equal("/guide/", await RenderAsync("{{ '/fr/guide/' | locale_url }}", locales: Site));

        Assert.Equal("ja", await RenderAsync("{{ '/ja/guide/' | locale_of }}", locales: Site));
        Assert.Equal("en", await RenderAsync("{{ '/japan/' | locale_of }}", locales: Site));
        Assert.Equal("en", await RenderAsync("{{ '/' | locale_of }}", locales: Site));
    }

    [Fact]
    public async Task LocalePagesExpandsFallbacks()
    {
        static NodeObject Item(string directory, string url) => new() { ["data"] = new NodeValue<Metadata>(new Metadata(new NodeObject { ["url"] = NodeValue.Create(url) }, directory)) };

        var all = new NodeArray(
            Item("/docs/", "/guide/index.html"),
            Item("/docs/ja/", "/ja/guide/index.html"),
            Item("/", "/about/index.html"));

        var scopes = Scopes(new NodeObject { ["collections"] = new NodeObject { ["all"] = all } }, Site);

        var text = await RenderAsync("{% assign list = collections.all | locale_pages %}{% for p in list %}{{ p.locale }} {{ p.url }} {{ p.fallback }} {{ p.data.url }};{% endfor %}", Page("/"), scopes, Site);

        Assert.Equal(
            "en /guide/ false /guide/index.html;fr /fr/guide/ true /guide/index.html;ja /ja/guide/ false /ja/guide/index.html;en /about/ false /about/index.html;",
            text);

        var noFallback = Locales("default: en\nroot: docs\nlanguages: { ja: {}, fr: {} }");
        Assert.Equal("3", await RenderAsync("{{ collections.all | locale_pages | size }}", Page("/"), Scopes(new NodeObject { ["collections"] = new NodeObject { ["all"] = all } }, noFallback), noFallback));
    }
}

public class LocaleFiltersSiteTests : CacheTestBase
{
    [Fact]
    public async Task MissingKeyIsTheKeyWithAWarningOncePerKey()
    {
        await Source.WriteTextFileAsync("/_data/i18n/en.yml", "hello: Hello");
        await Source.WriteTextFileAsync("/_content/a.html", "{{ 'missing' | t }} {{ 'hello' | t }}");
        await Source.WriteTextFileAsync("/_content/b.html", "{{ 'missing' | t }}");
        Site = await SiteOptions.DetectAsync(Source, Output, null, null, WorkflowLoader.Load("site:\n  locales:\n    default: en\n", "douze.yml").Site);

        await BuildCachedAsync(cache: false);

        Assert.Equal("missing Hello", await Output.ReadAllTextAsync("/a/index.html"));
        Assert.Equal("missing", await Output.ReadAllTextAsync("/b/index.html"));
        Assert.Single(Messages, x => x.Level == LogLevel.Warning && x.Message.Contains("'missing'"));
    }
}
