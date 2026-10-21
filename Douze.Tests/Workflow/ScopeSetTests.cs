using Douze.FileSystems;
using Douze.Nodes;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow;

public class ScopeSetTests
{
    private static NodeObject Values(string key, string value) => new() { [key] = NodeValue.Create(value) };

    private static FolderScope Folder(FPath directory, string key, string value) => new(directory, Values(key, value));

    private static string? Resolve(ScopeSet scopes, Metadata meta, string key, NodeObject? fallback = null)
    {
        return scopes.TryResolve(meta, key, fallback, null, out var value, out _) ? value?.GetValue<string>() : null;
    }

    [Fact]
    public void OwnWinsOverFolder()
    {
        var scopes = ScopeSet.Empty.WithFolders([Folder("/blog/", "layout", "folder.html")]);
        var meta = new Metadata(Values("layout", "own.html"), "/blog/");

        Assert.True(scopes.TryResolve(meta, "layout", null, null, out var value, out var hit));
        Assert.Equal("own.html", value!.GetValue<string>());
        Assert.Equal(ScopeKind.Own, hit.Kind);
    }

    [Fact]
    public void NearestFolderWins()
    {
        var scopes = ScopeSet.Empty.WithFolders([Folder("/", "layout", "root.html"), Folder("/blog/", "layout", "blog.html")]);

        Assert.Equal("blog.html", Resolve(scopes, new Metadata(new NodeObject(), "/blog/2020/"), "layout"));
        Assert.Equal("root.html", Resolve(scopes, new Metadata(new NodeObject(), "/docs/"), "layout"));

        Assert.True(scopes.TryResolve(new Metadata(new NodeObject(), "/blog/2020/"), "layout", null, null, out _, out var hit));
        Assert.Equal(new ScopeHit(ScopeKind.Folder, "/blog/"), hit);
    }

    [Fact]
    public void FolderWinsOverSite()
    {
        var scopes = ScopeSet.Empty
            .WithSite(new SiteScope("data", Values("layout", "site.html")))
            .WithFolders([Folder("/blog/", "layout", "blog.html")]);

        Assert.Equal("blog.html", Resolve(scopes, new Metadata(new NodeObject(), "/blog/"), "layout"));
        Assert.Equal("site.html", Resolve(scopes, new Metadata(new NodeObject(), "/docs/"), "layout"));
    }

    [Fact]
    public void SiteScopesResolveNearestContributionFirst()
    {
        var data = ScopeSet.Empty.WithSite(new SiteScope("data", new NodeObject { ["title"] = NodeValue.Create("data"), ["only"] = NodeValue.Create("data") }));
        var far = ScopeSet.Empty.WithSite(new SiteScope("step:far", Values("title", "far")));
        var near = ScopeSet.Empty.WithSite(new SiteScope("step:near", Values("title", "near")));

        // Contributions are given nearest first.
        var merged = ScopeSet.Merge([near, far], data);
        var meta = new Metadata(new NodeObject(), "/");

        Assert.Equal(["step:near", "step:far", "data"], merged.Site.Select(x => x.Source));
        Assert.Equal("near", Resolve(merged, meta, "title"));
        Assert.Equal("data", Resolve(merged, meta, "only"));

        // A later WithSite takes precedence.
        Assert.Equal("latest", Resolve(merged.WithSite(new SiteScope("step:latest", Values("title", "latest"))), meta, "title"));
    }

    [Fact]
    public void FallbackIsLast()
    {
        var scopes = ScopeSet.Empty.WithSite(new SiteScope("data", Values("title", "site")));
        var fallback = new NodeObject { ["title"] = NodeValue.Create("layout"), ["author"] = NodeValue.Create("layout") };
        var meta = new Metadata(new NodeObject(), "/");

        Assert.Equal("site", Resolve(scopes, meta, "title", fallback));
        Assert.Equal("layout", Resolve(scopes, meta, "author", fallback));

        Assert.True(scopes.TryResolve(meta, "author", fallback, null, out _, out var hit));
        Assert.Equal(ScopeKind.Fallback, hit.Kind);

        Assert.False(scopes.TryResolve(meta, "missing", fallback, null, out var missing, out hit));
        Assert.Null(missing);
        Assert.Equal(ScopeKind.None, hit.Kind);
    }

    [Fact]
    public void SettingsAreBelowEveryScopeAndCompleteObjects()
    {
        var settings = ScopeSet.FromSettings(new NodeObject
        {
            ["site"] = new NodeObject { ["url"] = NodeValue.Create("https://example.com"), ["title"] = NodeValue.Create("settings") },
            ["layout"] = NodeValue.Create("settings.html"),
        });

        var data = ScopeSet.Empty.WithSite(new SiteScope("step:site-data", new NodeObject
        {
            ["site"] = new NodeObject { ["title"] = NodeValue.Create("data"), ["author"] = NodeValue.Create("me") },
        }));

        var meta = new Metadata(new NodeObject(), "/");

        // Alone, the settings resolve like a site scope.
        Assert.True(settings.TryResolve(meta, "site", null, null, out var site, out var hit));
        Assert.Equal(new ScopeHit(ScopeKind.Site, ScopeSet.SettingsSource), hit);
        Assert.Equal("https://example.com", site!["url"]!.GetValue<string>());

        // Under a contribution, an object of the same key is completed: the contribution's properties win.
        var merged = ScopeSet.Merge([data], settings);
        var deps = new DependencyTracker();
        Assert.True(merged.TryResolve(meta, "site", null, deps, out site, out hit));
        Assert.Equal(new ScopeHit(ScopeKind.Site, "step:site-data"), hit);
        Assert.Equal(("https://example.com", "data", "me"), (site!["url"]!.GetValue<string>(), site["title"]!.GetValue<string>(), site["author"]!.GetValue<string>()));
        Assert.Equal(merged.ResolveHash("/", "site"), Assert.Single(deps.ToSet().Lookups).Hash);

        // The completed object is built once and shared; the data's own object is not modified.
        Assert.True(merged.TryResolve(meta, "site", null, null, out var again, out _));
        Assert.Same(site, again);
        Assert.False(((NodeObject)data.Site[0].Values["site"]!).TryGetItem("url", out _));

        // A value that is not an object hides the setting, as in any other scope.
        Assert.Equal("own.html", Resolve(merged, new Metadata(Values("layout", "own.html"), "/"), "layout"));
        Assert.Equal("settings.html", Resolve(merged, meta, "layout"));

        // Settings are part of the hashes: of the set, and of what a key they complete resolves to.
        Assert.NotEqual(ScopeSet.Empty.Hash, settings.Hash);
        Assert.NotEqual(data.Hash, merged.Hash);
        Assert.NotEqual(data.ResolveHash("/", "site"), merged.ResolveHash("/", "site"));
    }

    [Fact]
    public void RecordsWhatKeysResolveToBeyondOwn()
    {
        // Resolution order for a file in /blog/: own, the /blog/ folder scope, then "step:collections", then "data".
        var colors = new NodeArray(NodeValue.Create("red"));
        var scopes = ScopeSet.Empty
            .WithSite(new SiteScope("data", new NodeObject { ["colors"] = colors, ["characters"] = new NodeArray() }))
            .WithSite(new SiteScope("step:collections", new NodeObject { ["collections"] = new NodeObject() }))
            .WithFolders([Folder("/blog/", "layout", "blog.html")]);

        var meta = new Metadata(new NodeObject(), "/blog/");
        var deps = new DependencyTracker();

        Assert.True(scopes.TryResolve(meta, "colors", null, deps, out _, out var hit));
        Assert.Equal(new ScopeHit(ScopeKind.Site, "data"), hit);
        Assert.True(scopes.TryResolve(meta, "layout", null, deps, out _, out hit));
        Assert.Equal(new ScopeHit(ScopeKind.Folder, "/blog/"), hit);
        Assert.False(scopes.TryResolve(meta, "missing", null, deps, out _, out _));

        // Own values are part of the input refs: nothing to record.
        Assert.True(scopes.TryResolve(new Metadata(Values("title", "own"), "/blog/"), "title", null, deps, out _, out _));

        Assert.Equal(
            [("/blog/", "colors", NodeHash.Of(colors)), ("/blog/", "layout", NodeHash.Of(NodeValue.Create("blog.html"))), ("/blog/", "missing", DependencySet.Absent)],
            deps.ToSet().Lookups.Select(x => (x.Directory.FullName, x.Key, x.Hash)));
    }

    [Fact]
    public void ResolveHashChangesOnlyWithWhatTheKeyResolvesTo()
    {
        var data = new SiteScope("data", new NodeObject { ["layout"] = NodeValue.Create("site.html"), ["menu"] = new NodeArray() });
        var scopes = ScopeSet.Empty.WithSite(data);
        var hash = scopes.ResolveHash("/blog/2020/", "layout");

        // Another key changing, or a folder scope that does not define the key, changes nothing.
        Assert.Equal(hash, ScopeSet.Empty.WithSite(new SiteScope("data", new NodeObject { ["layout"] = NodeValue.Create("site.html"), ["menu"] = new NodeArray(NodeValue.Create("home")) })).ResolveHash("/blog/2020/", "layout"));
        Assert.Equal(hash, scopes.WithFolders([Folder("/blog/", "title", "Blog")]).ResolveHash("/blog/2020/", "layout"));
        Assert.Equal(hash, scopes.WithFolders([Folder("/docs/", "layout", "docs.html")]).ResolveHash("/blog/2020/", "layout"));

        // A folder scope on the path that defines the key shadows the site value, even in a directory that had no
        // folder scope before; a new value changes the hash too.
        Assert.NotEqual(hash, scopes.WithFolders([Folder("/blog/", "layout", "blog.html")]).ResolveHash("/blog/2020/", "layout"));
        Assert.NotEqual(hash, ScopeSet.Empty.WithSite(new SiteScope("data", Values("layout", "other.html"))).ResolveHash("/blog/2020/", "layout"));

        // A step contribution that defines the key later shadows it too.
        Assert.NotEqual(hash, scopes.WithSite(new SiteScope("step:collections", Values("layout", "step.html"))).ResolveHash("/blog/2020/", "layout"));
        Assert.Equal(DependencySet.Absent, scopes.ResolveHash("/", "missing"));
    }
}
