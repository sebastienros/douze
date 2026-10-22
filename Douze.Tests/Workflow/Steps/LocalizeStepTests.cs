using Douze.FileSystems;
using Douze.Nodes;
using Douze.Workflow;
using Douze.Workflow.Yaml;
using Xunit;

namespace Douze.Tests.Workflow.Steps;

public class LocalizeStepTests : StepTestBase
{
    private static WorkflowDefinition Localize() => Workflow(
        Step("pages", "frontmatter", inputs: ["**"]),
        Step("permalink", "permalink", needs: ["pages"]),
        Step("collections", "collect", needs: ["permalink"]),
        Step("localize", "localize", needs: ["permalink", "collections"]));

    private SiteSettings _settings = SiteSettings.None;

    private async Task WriteSiteAsync(bool fallback)
    {
        _settings = WorkflowLoader.Load($"""
            site:
              locales:
                default: en
                root: docs
                fallback: {(fallback ? "true" : "false")}
                languages:
                  en: {"{"} label: English {"}"}
                  ja: {"{"} label: 日本語, lang: ja {"}"}
                  ar: {"{"} lang: ar, dir: rtl {"}"}
            """, "douze.yml").Site;

        await Source.WriteTextFileAsync("/docs/index.md", "home");
        await Source.WriteTextFileAsync("/docs/guide.md", "guide");
        await Source.WriteTextFileAsync("/docs/ja/guide.md", "ガイド");
        await Source.WriteTextFileAsync("/about.md", "about");
        await DetectAsync();
    }

    // Once the files are written: the folders are detected.
    private async Task DetectAsync() => Site = await SiteOptions.DetectAsync(Source, Output, null, null, _settings);

    private static VirtualFile File(BuildResult result, string path) => Outputs(result["localize"]).Single(x => x.Path == path);

    private static string Own(VirtualFile file, string key) => file.Metadata.Own[key]!.GetValue<string>();

    private static string[] Alternates(VirtualFile file) =>
        [.. file.Metadata.Own["alternates"]!.AsArray().Select(x => $"{x!["locale"]!.GetValue<string>()} {x["lang"]!.GetValue<string>()} {x["url"]!.GetValue<string>()}")];

    [Fact]
    public async Task SetsTheLocaleOfRootAndTranslatedPages()
    {
        await WriteSiteAsync(fallback: false);

        var result = await BuildSuccessfullyAsync(Localize());

        // The locale root is removed from the urls, the locale folder stays.
        var guide = File(result, "/guide/index.md");
        Assert.Equal("/guide/index.html", Own(guide, "url"));
        Assert.Equal("en", Own(guide, "locale"));
        Assert.Equal("en", Own(guide, "lang"));
        Assert.Equal("ltr", Own(guide, "dir"));
        Assert.Equal("/guide/", Own(guide, "locale_url"));
        Assert.Equal(["en en /guide/", "ja ja /ja/guide/"], Alternates(guide));

        var ja = File(result, "/ja/guide/index.md");
        Assert.Equal("/ja/guide/index.html", Own(ja, "url"));
        Assert.Equal("ja", Own(ja, "locale"));
        Assert.Equal("/guide/", Own(ja, "locale_url"));
        Assert.Equal(["en en /guide/", "ja ja /ja/guide/"], Alternates(ja));

        var home = File(result, "/index.md");
        Assert.Equal("/", Own(home, "locale_url"));
        Assert.Equal(["en en /"], Alternates(home));

        // No copies without fallback.
        Assert.Equal(["/about/index.md", "/guide/index.md", "/index.md", "/ja/guide/index.md"], Outputs(result["localize"]).Select(x => x.Path.FullName));
    }

    [Fact]
    public async Task PagesOutsideTheRootPassThrough()
    {
        await WriteSiteAsync(fallback: true);

        var result = await BuildSuccessfullyAsync(Localize());

        var about = File(result, "/about/index.md");
        Assert.Same(Outputs(result["permalink"]).Single(x => x.Path == "/about/index.md"), about);
        Assert.Null(about.Metadata.Own["locale"]);
    }

    [Fact]
    public async Task FallbackCopiesForTheMissingLocalesOnly()
    {
        await WriteSiteAsync(fallback: true);

        var result = await BuildSuccessfullyAsync(Localize());

        Assert.Equal(
            ["/about/index.md", "/ar/guide/index.md", "/ar/index.md", "/guide/index.md", "/index.md", "/ja/guide/index.md", "/ja/index.md"],
            Outputs(result["localize"]).Select(x => x.Path.FullName));

        var guide = File(result, "/guide/index.md");
        var copy = File(result, "/ar/guide/index.md");

        Assert.Same(guide.Content, copy.Content);
        Assert.Equal("ar", Own(copy, "locale"));
        Assert.Equal("ar", Own(copy, "lang"));
        Assert.Equal("rtl", Own(copy, "dir"));
        Assert.Equal("/ar/guide/index.html", Own(copy, "url"));
        Assert.Equal("/guide/index.html", Own(copy, "original_url"));
        Assert.Equal("/guide/", Own(copy, "locale_url"));
        Assert.True(copy.Metadata.Own["fallback"]!.GetValue<bool>());
        Assert.Equal(guide.Metadata.Directory, copy.Metadata.Directory);

        // The translated page is not copied, and is the only one at its url.
        Assert.Single(Outputs(result["localize"]), x => x.Path == "/ja/guide/index.md");
        Assert.Null(File(result, "/ja/guide/index.md").Metadata.Own["fallback"]);
    }

    [Fact]
    public async Task DependsOnThePresenceOfTheUrlsItLookedFor()
    {
        await WriteSiteAsync(fallback: false);

        var result = await BuildSuccessfullyAsync(Localize());

        var unit = result["localize"].Results.Single(x => x.Inputs.Single().Path == "/guide/index.md");
        var lookup = Assert.Single(unit.Dependencies.Lookups);
        Assert.Equal("collections", lookup.Key);

        var urls = Assert.Single(lookup.Access!.Members);
        Assert.Equal("urls", urls.Key);
        Assert.Equal(["/ar/guide/", "/ja/guide/"], urls.Access.Members.Select(x => x.Key));
        Assert.All(urls.Access.Members, x => Assert.Same(ValueAccess.Untouched, x.Access));
    }

    [Fact]
    public async Task WithoutLocalesEveryPagePassesThrough()
    {
        await Source.WriteTextFileAsync("/index.md", "home");

        var result = await BuildSuccessfullyAsync(Localize());

        Assert.Same(Outputs(result["permalink"]).Single(), Outputs(result["localize"]).Single());
    }

    [Fact]
    public async Task RenderedPerLocale()
    {
        await WriteSiteAsync(fallback: true);
        await Source.WriteTextFileAsync("/_data/i18n/en.yml", "notice: Not translated");
        await Source.WriteTextFileAsync("/_data/i18n/ar.yml", "notice: Untranslated");
        await Source.WriteTextFileAsync("/_includes/page.html", "<html lang=\"{{ lang }}\" dir=\"{{ dir }}\">{% if fallback %}{{ 'notice' | t }} {% endif %}{{ content }}</html>");
        await Source.WriteTextFileAsync("/_data/layout.yml", "page.html");
        await DetectAsync();

        var result = await BuildSuccessfullyAsync(Workflow(
            Step("data", "data", inputs: ["_data/**"], with: With(("scope", NodeValue.Create("site")))),
            Step("pages", "frontmatter", inputs: ["docs/**"], needs: ["data"]),
            Step("permalink", "permalink", needs: ["pages"]),
            Step("collections", "collect", needs: ["permalink"]),
            Step("localize", "localize", needs: ["permalink", "collections"]),
            Step("markdown", "markdown", needs: ["localize"]),
            Step("layout", "layout", needs: ["markdown"]),
            Step("write", "write", needs: ["layout"])));

        Assert.Equal("<html lang=\"ar\" dir=\"rtl\">Untranslated <p>guide</p>\n</html>", await Output.ReadAllTextAsync("/ar/guide/index.html"));
        Assert.Equal("<html lang=\"ja\" dir=\"ltr\">Not translated <p>home</p>\n</html>", await Output.ReadAllTextAsync("/ja/index.html"));
        Assert.Equal("<html lang=\"ja\" dir=\"ltr\"><p>ガイド</p>\n</html>", await Output.ReadAllTextAsync("/ja/guide/index.html"));
        Assert.Equal("<html lang=\"en\" dir=\"ltr\"><p>guide</p>\n</html>", await Output.ReadAllTextAsync("/guide/index.html"));
    }
}
