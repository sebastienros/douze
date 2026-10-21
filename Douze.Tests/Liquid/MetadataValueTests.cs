using Douze.FileSystems;
using Douze.Liquid;
using Douze.Nodes;
using Douze.Workflow;
using Fluid;
using Xunit;

namespace Douze.Tests.Liquid;

public class MetadataValueTests
{
    internal static async Task<string> RenderAsync(string template, Metadata metadata, ScopeSet scopes, DependencyTracker? deps = null)
    {
        var loader = new TemplateLoader(new MemoryFileSystem(), ["/_includes/"], LiquidOptions.CreateParser());
        var context = new TemplateContext(new MetadataValue(metadata), LiquidOptions.Create(loader));
        context.AmbientValues[AmbientKeys.Scopes] = scopes;
        context.AmbientValues[AmbientKeys.Dependencies] = deps ?? DependencyTracker.Null;

        var parsed = loader.GetOrParse(Hash128.OfText(template), template, "test");
        return await parsed.Template.RenderAsync(context);
    }

    [Fact]
    public async Task ResolvesThroughAmbientScopes()
    {
        var scopes = ScopeSet.Empty
            .WithSite(new SiteScope("data", new NodeObject { ["site"] = new NodeObject { ["name"] = NodeValue.Create("Douze") } },
                new Dictionary<string, string> { ["site"] = "/_data/site.yml" }))
            .WithFolders([new FolderScope("/blog/", new NodeObject { ["layout"] = NodeValue.Create("blog.html") }, ["/blog/blog.yml"])]);

        var meta = new Metadata(new NodeObject { ["title"] = NodeValue.Create("Hello") }, "/blog/");
        var deps = new DependencyTracker();

        Assert.Equal("Hello Douze blog.html", await RenderAsync("{{ title }} {{ site.name }} {{ layout }}", meta, scopes, deps));
        Assert.Equal(["/_data/site.yml", "/blog/blog.yml"], deps.ToSet().DataFiles.Select(x => x.FullName));

        // Locals shadow the model.
        Assert.Equal("local", await RenderAsync("{% assign title = 'local' %}{{ title }}", meta, scopes));
    }

    [Fact]
    public async Task MissIsNil()
    {
        var meta = new Metadata(new NodeObject(), "/");

        Assert.Equal("[]", await RenderAsync("[{{ missing }}]", meta, ScopeSet.Empty));
        Assert.Equal("nil", await RenderAsync("{% if missing == nil %}nil{% endif %}", meta, ScopeSet.Empty));
        Assert.Equal("none", await RenderAsync("{{ missing.deep | default: 'none' }}", meta, ScopeSet.Empty));
    }

    [Fact]
    public async Task NodeValueOfMetadataConverts()
    {
        var article = new Metadata(new NodeObject { ["title"] = NodeValue.Create("Article") }, "/articles/");
        var item = new NodeObject { ["data"] = new NodeValue<Metadata>(article) };

        // Nested metadata resolves through the same scopes, with its own directory.
        var scopes = ScopeSet.Empty
            .WithSite(new SiteScope("step:collections", new NodeObject { ["collections"] = new NodeObject { ["all"] = new NodeArray(item) } }))
            .WithFolders([new FolderScope("/articles/", new NodeObject { ["layout"] = NodeValue.Create("articles.html") }, [])]);

        var meta = new Metadata(new NodeObject(), "/");

        Assert.Equal("Article articles.html;", await RenderAsync("{% for x in collections.all %}{{ x.data.title }} {{ x.data.layout }};{% endfor %}", meta, scopes));
        Assert.IsType<MetadataValue>(NodeValues.Convert(new NodeValue<Metadata>(article)));
        Assert.IsType<MetadataValue>(NodeValues.Convert(article));
    }
}
