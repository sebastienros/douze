using Douze.FileSystems;
using Douze.Nodes;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow;

public class ScopeSetTests
{
    private static NodeObject Values(string key, string value) => new() { [key] = NodeValue.Create(value) };

    private static FolderScope Folder(FPath directory, string key, string value) => new(directory, Values(key, value), [FPath.Combine(directory, "data.yml")]);

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
    public void RecordsDependenciesForDataKeysStepScopesAndMisses()
    {
        var scopes = ScopeSet.Empty
            .WithSite(new SiteScope("data", new NodeObject { ["colors"] = new NodeArray(), ["characters"] = new NodeArray() },
                new Dictionary<string, string> { ["colors"] = "/_data/colors.json", ["characters"] = "/_data/characters.yml" }))
            .WithSite(new SiteScope("step:collections", new NodeObject { ["collections"] = new NodeObject() }))
            .WithFolders([new FolderScope("/blog/", Values("layout", "blog.html"), ["/blog/blog.yml"])]);

        var meta = new Metadata(new NodeObject(), "/blog/");

        var dataKey = new DependencyTracker();
        Assert.True(scopes.TryResolve(meta, "colors", null, dataKey, out _, out var hit));
        Assert.Equal(new ScopeHit(ScopeKind.Site, "/_data/colors.json"), hit);
        Assert.Equal(["/_data/colors.json"], dataKey.ToSet().DataFiles.Select(x => x.FullName));
        Assert.Empty(dataKey.ToSet().Scopes);

        var stepScope = new DependencyTracker();
        Assert.True(scopes.TryResolve(meta, "collections", null, stepScope, out _, out hit));
        Assert.Equal(new ScopeHit(ScopeKind.Site, "step:collections"), hit);
        Assert.Equal(["step:collections"], stepScope.ToSet().Scopes);
        Assert.Empty(stepScope.ToSet().DataFiles);

        var folder = new DependencyTracker();
        Assert.True(scopes.TryResolve(meta, "layout", null, folder, out _, out _));
        Assert.Equal(["/blog/blog.yml"], folder.ToSet().DataFiles.Select(x => x.FullName));

        // A miss depends on every scope that could define the key.
        var miss = new DependencyTracker();
        Assert.False(scopes.TryResolve(meta, "missing", null, miss, out _, out _));
        Assert.Equal(["data", "step:collections"], miss.ToSet().Scopes);
        Assert.Equal(["/blog/blog.yml"], miss.ToSet().DataFiles.Select(x => x.FullName));

        // Own values are part of the input refs: nothing to record.
        var own = new DependencyTracker();
        Assert.True(scopes.TryResolve(new Metadata(Values("colors", "own"), "/blog/"), "colors", null, own, out _, out _));
        Assert.True(own.ToSet().IsEmpty);
    }
}
