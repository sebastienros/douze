using System.Xml.Linq;
using Douze.FileSystems;
using Douze.Nodes;
using Douze.Progress;
using Douze.Tests.Workflow.Steps;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.DotNetApi;

public sealed class DotNetApiStepTests : DotNetApiTestBase
{
    private async Task<NodeObject> BuildApiAsync(NodeObject? with = null)
    {
        await CopySampleAsync();
        return ApiValue(await BuildSuccessfullyAsync(StepTestBase.Workflow(Api(with))));
    }

    [Fact]
    public async Task AssembliesAndNamespaces()
    {
        var api = await BuildApiAsync();

        Assert.Equal("/api/", Text(api, "url"));
        var assembly = Assert.Single((NodeArray)api["assemblies"]!)!;
        Assert.Equal("Contoso.Widgets", Text(assembly, "name"));
        Assert.Equal("2.1.0.0", Text(assembly, "version"));
        Assert.StartsWith("2.1.0", Text(assembly, "informationalVersion"));
        Assert.StartsWith(".NETCoreApp,Version=v", Text(assembly, "targetFramework"));

        var namespaces = (NodeArray)api["namespaces"]!;
        Assert.Equal(["Contoso.Widgets", "Contoso.Widgets.Drawing"], namespaces.Select(x => Text(x, "name")));

        // A namespace is documented by its NamespaceDoc class, which is not part of the API, like internal types.
        var widgets = namespaces[0]!;
        Assert.Equal("Widgets and the parts they are made of.", Text(widgets, "summary"));
        Assert.Equal("/api/contoso.widgets/", Text(widgets, "url"));
        Assert.Equal("Contoso.Widgets namespace", Text(widgets, "title"));
        var names = ((NodeArray)widgets["types"]!).Select(x => Text(x, "displayName")).ToList();
        Assert.Equal(["Bag<T>", "Bag<T>.Entry<TKey>", "Color", "Gadget", "IConverter<TIn, TOut>", "IShape", "NumberConverter", "Options", "Person", "Size", "SizeChangedEventArgs", "SpanReader", "Widget", "Widget.Part", "WidgetBase", "WidgetExtensions", "WidgetHandler<T, TResult>"], names);

        // Every type, by documentation comment ID, in api.types and api.ids.
        var ids = (NodeObject)api["ids"]!;
        Assert.Same(Type(api, "Contoso.Widgets.Widget"), ids["T:Contoso.Widgets.Widget"]);
        Assert.Same(widgets, ids["N:Contoso.Widgets"]);
        Assert.Equal(18, ((NodeArray)api["types"]!).Count);
    }

    [Fact]
    public async Task DocumentationIdsMatchTheCompiler()
    {
        var api = await BuildApiAsync();
        var ids = (NodeObject)api["ids"]!;

        // The compiler documents every member; what is not in the API: internal and private members, the protected
        // members of a sealed class, the class that documents a namespace. Explicit implementations of generic
        // interfaces are found, with their metadata names (IConverter{nint,System#String}).
        var documented = XDocument.Load(SampleXml).Root!.Element("members")!.Elements("member").Select(x => (string)x.Attribute("name")!);
        var missing = documented.Where(x => !ids.ContainsKey(x)).Order(StringComparer.Ordinal);

        Assert.Equal(
        [
            "M:Contoso.Widgets.Gadget.OnChanged",
            "M:Contoso.Widgets.Widget.Hidden",
            "T:Contoso.Widgets.NamespaceDoc",
        ], missing);
    }

    [Fact]
    public async Task Types()
    {
        var api = await BuildApiAsync();

        var widget = Type(api, "Contoso.Widgets.Widget");
        Assert.Equal("class", Text(widget, "kind"));
        Assert.Equal("T:Contoso.Widgets.Widget", Text(widget, "id"));
        Assert.Equal("Widget class", Text(widget, "title"));
        Assert.Equal("Contoso.Widgets", Text(widget, "namespace"));
        Assert.Equal("Contoso.Widgets", Text(widget, "assembly"));
        Assert.Equal("/api/contoso.widgets.widget/", Text(widget, "url"));
        Assert.Equal("public class Widget : WidgetBase, IShape, IEquatable<Widget>, IDisposable", Text(widget, "signature"));
        Assert.Equal(["object", "WidgetBase"], ((NodeArray)widget["inheritance"]!).Select(x => Text(x, "name")));
        Assert.Equal(["Gadget", "Widget"], ((NodeArray)Type(api, "Contoso.Widgets.WidgetBase")["derivedTypes"]!).Select(x => Text(x, "name")));
        Assert.Equal(["Widget.Part"], ((NodeArray)widget["nestedTypes"]!).Select(x => Text(x, "name")));
        Assert.Equal("Widget", Text(Type(api, "Contoso.Widgets.Widget.Part")["declaringType"], "name"));

        Assert.Equal("public static class WidgetExtensions", Text(Type(api, "Contoso.Widgets.WidgetExtensions"), "signature"));
        Assert.Equal("public abstract class WidgetBase", Text(Type(api, "Contoso.Widgets.WidgetBase"), "signature"));
        Assert.Equal("public sealed class Gadget : WidgetBase", Text(Type(api, "Contoso.Widgets.Gadget"), "signature"));
        Assert.Equal("public interface IShape", Text(Type(api, "Contoso.Widgets.IShape"), "signature"));
        Assert.Equal("public enum Color : byte", Text(Type(api, "Contoso.Widgets.Color"), "signature"));
        Assert.Equal("public ref struct SpanReader", Text(Type(api, "Contoso.Widgets.SpanReader"), "signature"));
        Assert.Equal("public readonly record struct Size : IEquatable<Size>", Text(Type(api, "Contoso.Widgets.Size"), "signature"));
        Assert.Equal("record struct", Text(Type(api, "Contoso.Widgets.Size"), "kind"));
        Assert.Equal("public record Person : IEquatable<Person>", Text(Type(api, "Contoso.Widgets.Person"), "signature"));
        Assert.Equal("public class Bag<T> : IEnumerable<T>, IEnumerable", Text(Type(api, "Contoso.Widgets.Bag<T>"), "signature"));
        Assert.Equal("Bag<T>.Entry<TKey>", Text(Type(api, "Contoso.Widgets.Bag<T>.Entry<TKey>"), "displayName"));
        Assert.Equal("/api/contoso.widgets.bag-1.entry-1/", Text(Type(api, "Contoso.Widgets.Bag<T>.Entry<TKey>"), "url"));

        var handler = Type(api, "Contoso.Widgets.WidgetHandler<T, TResult>");
        Assert.Equal("delegate", Text(handler, "kind"));
        Assert.Equal("public delegate TResult WidgetHandler<in T, out TResult>(T widget) where T : Widget", Text(handler, "signature"));
        Assert.Equal("The widget.", Text(((NodeArray)handler["parameters"]!)[0], "description"));
        Assert.Equal("The result.", Text(handler["returns"], "description"));
        Assert.Equal(["in", "out"], ((NodeArray)handler["typeParameters"]!).Select(x => Text(x, "variance")));
        Assert.Equal("Widget", Text(((NodeArray)handler["typeParameters"]!)[0], "constraints"));

        // The equality contract of a record is not part of its API, its other members are.
        var person = Type(api, "Contoso.Widgets.Person");
        Assert.Equal(["Name"], ((NodeArray)person["properties"]!).Select(x => Text(x, "name")));
        Assert.Contains("public virtual bool Equals(Person? other)", ((NodeArray)person["methods"]!).Select(x => Text(x, "signature")));
    }

    [Fact]
    public async Task Members()
    {
        var api = await BuildApiAsync();
        var widget = Type(api, "Contoso.Widgets.Widget");

        string Signature(string list, string displayName) => Text(Member(widget, list, displayName), "signature")!;

        Assert.Equal("public Widget(string name = \"widget\")", Signature("constructors", "Widget(string)"));
        Assert.Equal("public const string DefaultName = \"widget\"", Signature("fields", "DefaultName"));
        Assert.Equal("public static readonly int MaxSize", Signature("fields", "MaxSize"));
        Assert.Equal("public event EventHandler<SizeChangedEventArgs>? Resized", Signature("events", "Resized"));
        Assert.Equal("public object? Tag { get; set; }", Signature("properties", "Tag"));
        Assert.Equal("public string Label { get; init; }", Signature("properties", "Label"));
        Assert.Equal("public int Width { get; protected set; }", Signature("properties", "Width"));
        Assert.Equal("public int this[int index] { get; }", Signature("properties", "this[int]"));
        Assert.Equal("double IShape.Perimeter { get; }", Signature("properties", "IShape.Perimeter"));
        Assert.Equal("public T Convert<T>(Func<Widget, T> converter) where T : class, new()", Signature("methods", "Convert<T>(Func<Widget, T>)"));
        Assert.Equal("public bool TryGet(out int value, ref int other, in Size size)", Signature("methods", "TryGet(out int, ref int, in Size)"));
        Assert.Equal("public void Add(params int[] values)", Signature("methods", "Add(int[])"));
        Assert.Equal("public void Paint(Color color = Color.Red, Options options = Options.None)", Signature("methods", "Paint(Color, Options)"));
        Assert.Equal("protected virtual void OnResized(SizeChangedEventArgs args)", Signature("methods", "OnResized(SizeChangedEventArgs)"));
        Assert.Equal("public override string Describe()", Signature("methods", "Describe()"));
        Assert.Equal("void IDisposable.Dispose()", Signature("methods", "IDisposable.Dispose()"));
        Assert.Equal("public static Widget operator +(Widget left, Widget right)", Signature("operators", "operator +(Widget, Widget)"));
        Assert.Equal("public static implicit operator string(Widget widget)", Signature("operators", "implicit operator string(Widget)"));
        Assert.Equal("public static ref readonly int First(ReadOnlySpan<int> span)", Text(Member(Type(api, "Contoso.Widgets.SpanReader"), "methods", "First(ReadOnlySpan<int>)"), "signature"));
        Assert.Equal("public T? Get(TKey key)", Text(Member(Type(api, "Contoso.Widgets.Bag<T>.Entry<TKey>"), "methods", "Get(TKey)"), "signature"));
        Assert.Equal("Red = 1", Text(Member(Type(api, "Contoso.Widgets.Color"), "fields", "Red"), "signature"));
        Assert.Equal("1", Text(Member(Type(api, "Contoso.Widgets.Color"), "fields", "Red"), "value"));

        // Members are sorted by kind, then name; the protected members of a sealed class are not part of its API.
        Assert.Equal(["Add(int[])", "Area()", "Convert<T>(Func<Widget, T>)", "Describe()", "Equals(Widget?)", "Equals(object?)", "GetHashCode()", "Grow()", "OnResized(SizeChangedEventArgs)", "Paint(Color, Options)", "Resize(Size)", "Resize(int, int)", "TryGet(out int, ref int, in Size)", "TryResize(int, int)", "IDisposable.Dispose()"],
            ((NodeArray)widget["methods"]!).Select(x => Text(x, "displayName")));
        Assert.Equal(["Describe()"], ((NodeArray)Type(api, "Contoso.Widgets.Gadget")["methods"]!).Select(x => Text(x, "displayName")));

        var named = Member(Type(api, "Contoso.Widgets.WidgetExtensions"), "methods", "Named(Widget, string)");
        Assert.Equal("public static Widget Named(this Widget widget, string name)", Text(named, "signature"));
        Assert.True(named["isExtension"]!.GetValue<bool>());

        var dispose = Member(widget, "methods", "IDisposable.Dispose()");
        Assert.True(dispose["isExplicit"]!.GetValue<bool>());
        Assert.Equal("IDisposable", Text(dispose["explicitInterface"], "name"));
        Assert.Equal("", Text(dispose, "accessibility"));

        var converter = Type(api, "Contoso.Widgets.NumberConverter");
        Assert.Equal(["string IConverter<nint, string>.Convert(nint value)", "Bag<int> IConverter<string, Bag<int>>.Convert(string value)"],
            ((NodeArray)converter["methods"]!).Select(x => Text(x, "signature")));
        Assert.Equal("Writes a native integer.", Text(((NodeArray)converter["methods"]!)[0], "summary"));

        var grow = Member(widget, "methods", "Grow()");
        Assert.Equal("Use Resize instead.", Text(grow["obsolete"], "message"));
        Assert.Equal(["override"], ((NodeArray)Member(widget, "methods", "Describe()")["modifiers"]!).Select(x => x!.GetValue<string>()));
        Assert.True(Member(widget, "methods", "Describe()")["isOverride"]!.GetValue<bool>());
    }

    [Fact]
    public async Task TypeReferencesLink()
    {
        var api = await BuildApiAsync();
        var widget = Type(api, "Contoso.Widgets.Widget");
        var convert = Member(widget, "methods", "Convert<T>(Func<Widget, T>)");
        var parameter = ((NodeArray)convert["parameters"]!).Single()!;

        Assert.Equal("converter", Text(parameter, "name"));
        Assert.Equal("Converts.", Text(parameter, "description"));
        var type = parameter["type"]!;
        Assert.Equal("Func<Widget, T>", Text(type, "name"));
        Assert.Equal("System.Func<Contoso.Widgets.Widget, T>", Text(type, "fullName"));
        Assert.Equal("T:System.Func`2", Text(type, "id"));
        Assert.Equal("https://learn.microsoft.com/dotnet/api/system.func-2", Text(type, "url"));
        Assert.Equal("<a href=\"https://learn.microsoft.com/dotnet/api/system.func-2\">Func</a>&lt;<a href=\"/api/contoso.widgets.widget/\">Widget</a>, T&gt;", Text(type, "html"));

        var tryGet = Member(widget, "methods", "TryGet(out int, ref int, in Size)");
        Assert.Equal(["out", "ref", "in"], ((NodeArray)tryGet["parameters"]!).Select(x => Text(x, "modifier")));
        Assert.Equal("\"widget\"", Text(((NodeArray)Member(widget, "constructors", "Widget(string)")["parameters"]!)[0], "defaultValue"));
        Assert.Equal("bool", Text(tryGet["returns"]!["type"], "name"));
        Assert.Equal("Whether it worked.", Text(tryGet["returns"], "description"));
        Assert.Equal("string", Text(Member(widget, "properties", "Name")["type"], "name"));
    }

    [Fact]
    public async Task DocumentationIsHtml()
    {
        var api = await BuildApiAsync();
        var widget = Type(api, "Contoso.Widgets.Widget");

        Assert.Equal("A widget that can be <a href=\"/api/contoso.widgets.widget/#resize-system.int32-system.int32\">resized</a>.", Text(widget, "summary"));
        Assert.Equal("<p>The first paragraph.</p><p>The second one, with <code>code</code> and <code>null</code>.</p><ul><li><strong>One</strong> – the first item</li><li>the second item</li></ul>", Text(widget, "remarks"));
        Assert.Equal("<pre data-lang=\"csharp\"><code class=\"language-csharp\">var widget = new Widget(&quot;a&quot;);\nwidget.Resize(1, 2);</code></pre>", Text(widget, "example"));

        var seeAlso = (NodeArray)widget["seeAlso"]!;
        Assert.Equal("<a href=\"/api/contoso.widgets.ishape/\">IShape</a>", Text(seeAlso[0], "html"));
        Assert.Equal("Widget guide", Text(seeAlso[1], "name"));
        Assert.Equal("https://example.com/widgets", Text(seeAlso[1], "url"));

        var resize = Member(widget, "methods", "Resize(int, int)");
        Assert.Equal("<code>true</code> when the size changed.", Text(resize["returns"], "description"));
        var exception = ((NodeArray)resize["exceptions"]!).Single()!;
        Assert.Equal("ArgumentOutOfRangeException", Text(exception["type"], "name"));
        Assert.Equal("https://learn.microsoft.com/dotnet/api/system.argumentoutofrangeexception", Text(exception["type"], "url"));
        Assert.Equal("<code>width</code> is negative.", Text(exception, "description"));

        Assert.Equal("Resizes the widget to a <a href=\"/api/contoso.widgets.size/\"><code>Size</code></a>.", Text(Member(widget, "methods", "Resize(Size)"), "summary"));
        Assert.Equal("The name, never <code>null</code>.", Text(Member(widget, "properties", "Name"), "valueDescription"));
        Assert.Equal("The arguments of <a href=\"/api/contoso.widgets.widget/#resized\"><code>Widget.Resized</code></a>.", Text(Type(api, "Contoso.Widgets.SizeChangedEventArgs"), "summary"));
        Assert.Equal("The color, <a href=\"/api/contoso.widgets.color/#red\"><code>Color.Red</code></a> by default.",
            Text(((NodeArray)Member(Type(api, "Contoso.Widgets.Drawing.Canvas"), "methods", "Draw(Widget, Color)")["parameters"]!)[1], "description"));
        Assert.Equal("A bag of <code>T</code>, see <a href=\"https://learn.microsoft.com/dotnet/api/system.collections.generic.list-1\"><code>List&lt;T&gt;</code></a>.", Text(Type(api, "Contoso.Widgets.Bag<T>"), "summary"));
    }

    [Fact]
    public async Task InheritDoc()
    {
        var api = await BuildApiAsync();
        var widget = Type(api, "Contoso.Widgets.Widget");

        // From the abstract base method, the interface method, the interface property of an explicit implementation,
        // and a cref.
        Assert.Equal("Describes the widget.", Text(Member(widget, "methods", "Describe()"), "summary"));
        Assert.Equal("A description of the widget.", Text(Member(widget, "methods", "Describe()")["returns"], "description"));
        Assert.Equal("Computes the area.", Text(Member(widget, "methods", "Area()"), "summary"));
        Assert.Equal("Gets the perimeter.", Text(Member(widget, "properties", "IShape.Perimeter"), "summary"));
        Assert.Equal("Resizes the widget.", Text(Member(widget, "methods", "TryResize(int, int)"), "summary"));
        Assert.Equal("The new width.", Text(((NodeArray)Member(widget, "methods", "TryResize(int, int)")["parameters"]!)[0], "description"));

        // Object.Equals is documented by the documentation of System.Runtime, which the step was not given.
        Assert.Null(Member(widget, "methods", "Equals(object?)")["summary"]);
    }

    [Fact]
    public async Task InheritDocFromAnotherDocumentationFile()
    {
        await Source.WriteTextFileAsync("/lib/System.Runtime.xml", """
            <?xml version="1.0"?>
            <doc>
              <assembly><name>System.Runtime</name></assembly>
              <members>
                <member name="M:System.Object.Equals(System.Object)"><summary>Determines whether two objects are equal.</summary></member>
                <member name="M:System.IEquatable`1.Equals(`0)"><summary>Indicates whether the object is equal to another.</summary></member>
              </members>
            </doc>
            """);

        var api = await BuildApiAsync();
        var widget = Type(api, "Contoso.Widgets.Widget");

        Assert.Equal("Determines whether two objects are equal.", Text(Member(widget, "methods", "Equals(object?)"), "summary"));
        Assert.Equal("Indicates whether the object is equal to another.", Text(Member(widget, "methods", "Equals(Widget?)"), "summary"));
    }

    [Fact]
    public async Task MemberAnchors()
    {
        var api = await BuildApiAsync();
        var widget = Type(api, "Contoso.Widgets.Widget");

        // An overload's anchor has its parameters, a member without overloads its name only.
        Assert.Equal("ctor-system.string", Text(Member(widget, "constructors", "Widget(string)"), "anchor"));
        Assert.Equal("/api/contoso.widgets.widget/#tag", Text(Member(widget, "properties", "Tag"), "url"));
        Assert.Equal("/api/contoso.widgets.widget/#item", Text(Member(widget, "properties", "this[int]"), "url"));
        Assert.Equal("/api/contoso.widgets.widget/#system-idisposable-dispose", Text(Member(widget, "methods", "IDisposable.Dispose()"), "url"));
        Assert.Equal("/api/contoso.widgets.widget/#op_addition", Text(Member(widget, "operators", "operator +(Widget, Widget)"), "url"));

        // api.members has every member by name, with its overloads; without member pages, its url is the first one's.
        var resize = ((NodeArray)api["members"]!).Single(x => Text(x, "title") == "Widget.Resize method")!;
        Assert.Equal("/api/contoso.widgets.widget/#resize-contoso.widgets.size", Text(resize, "url"));
        Assert.Equal(["Resize(Size)", "Resize(int, int)"], ((NodeArray)resize["overloads"]!).Select(x => Text(x, "displayName")));
        Assert.Equal("Widget", Text(resize["type"], "name"));
    }

    [Fact]
    public async Task MemberPages()
    {
        var api = await BuildApiAsync(StepTestBase.With(("member-pages", NodeValue.Create(true)), ("url", NodeValue.Create("reference"))));
        var widget = Type(api, "Contoso.Widgets.Widget");

        Assert.Equal("/reference/contoso.widgets.widget/", Text(widget, "url"));
        Assert.Equal("/reference/contoso.widgets.widget.resize/#resize-system.int32-system.int32", Text(Member(widget, "methods", "Resize(int, int)"), "url"));
        Assert.Equal("/reference/contoso.widgets.widget.tag/#tag", Text(Member(widget, "properties", "Tag"), "url"));

        var groups = (NodeArray)api["members"]!;
        Assert.Equal("/reference/contoso.widgets.widget.-ctor/", Text(groups.Single(x => Text(x, "title") == "Widget constructor"), "url"));

        // The values of an enum are anchors of its page, not pages.
        Assert.Equal("/reference/contoso.widgets.color/#red", Text(Member(Type(api, "Contoso.Widgets.Color"), "fields", "Red"), "url"));
        Assert.DoesNotContain(groups, x => Text(x!["type"], "name") == "Color");
        Assert.Equal("/reference/contoso.widgets.widget.resize/", Text(groups.Single(x => Text(x, "title") == "Widget.Resize method"), "url"));
    }

    [Fact]
    public async Task ExternalDocumentation()
    {
        var api = await BuildApiAsync(StepTestBase.With(("external", new NodeObject { ["System.Collections"] = NodeValue.Create("https://docs.example.com/{name}") })));

        // Only the namespaces of the option link, with {name} the ID without its kind.
        Assert.Equal("https://docs.example.com/System.Collections.Generic.IEnumerable`1", Text(((NodeArray)Type(api, "Contoso.Widgets.Bag<T>")["interfaces"]!)[0], "url"));
        Assert.Null(Text(((NodeArray)Type(api, "Contoso.Widgets.Widget")["inheritance"]!)[0], "url"));

        api = await BuildApiAsync(StepTestBase.With(("external", new NodeObject())));
        Assert.Null(Text(((NodeArray)Type(api, "Contoso.Widgets.Bag<T>")["interfaces"]!)[0], "url"));
    }

    [Fact]
    public async Task PagesPaginateTheApi()
    {
        await CopySampleAsync();
        await Source.WriteTextFileAsync("/_content/api/index.html", """
            {% for ns in api.namespaces %}<a href="{{ ns.url }}">{{ ns.name }}</a>
            {% endfor %}
            """);
        await Source.WriteTextFileAsync("/_content/api/type.html", """
            ---
            pagination:
              data: api.types
              size: 1
              alias: type
            permalink: "{{ type.url }}"
            ---
            <h1>{{ type.title }}</h1>
            <pre><code>{{ type.signature }}</code></pre>
            {{ type.summary | raw }}
            {% for m in type.methods %}<h2 id="{{ m.anchor }}">{{ m.displayName }}</h2>{% for p in m.parameters %} {{ p.type.html | raw }} {{ p.name }}{% endfor %}
            {% endfor %}
            """);

        var result = await BuildSuccessfullyAsync(StepTestBase.Workflow(
            Api(),
            StepTestBase.Step("pages", "frontmatter", inputs: ["_content/**"], needs: ["api"]),
            StepTestBase.Step("permalink", "permalink", needs: ["pages"], with: StepTestBase.With(("root", NodeValue.Create("_content")))),
            StepTestBase.Step("paginate", "paginate", needs: ["permalink"], with: StepTestBase.With(("root", NodeValue.Create("_content")))),
            StepTestBase.Step("render", "liquid", needs: ["paginate"]),
            StepTestBase.Step("write", "write", needs: ["render"])));

        Assert.Equal("""
            <a href="/api/contoso.widgets/">Contoso.Widgets</a>
            <a href="/api/contoso.widgets.drawing/">Contoso.Widgets.Drawing</a>

            """, await Output.ReadAllTextAsync("/api/index.html"));

        var page = await Output.ReadAllTextAsync("/api/contoso.widgets.drawing.canvas/index.html");
        Assert.Equal("""
            <h1>Canvas class</h1>
            <pre><code>public static class Canvas</code></pre>
            Draws widgets on a <a href="/api/contoso.widgets.drawing.canvas/"><code>Canvas</code></a>.
            <h2 id="draw">Draw(Widget, Color)</h2> <a href="/api/contoso.widgets.widget/">Widget</a> widget <a href="/api/contoso.widgets.color/">Color</a> color

            """, page);

        // One page per type, and the dll and xml files are not written.
        Assert.Equal(18, result.Outputs.Count(x => x.Path.FullName.StartsWith("/api/contoso.", StringComparison.Ordinal)));
        Assert.DoesNotContain(result.Outputs, x => x.Path.FullName.EndsWith(".dll", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReplayedFromTheCache()
    {
        await CopySampleAsync();
        var workflow = StepTestBase.Workflow(Api());

        await BuildCachedAsync(workflow);
        var result = await BuildCachedAsync(workflow);
        Assert.Empty(Executed(result, "api"));
        Assert.Equal("Contoso.Widgets", Text(((NodeArray)ApiValue(result)["assemblies"]!)[0], "name"));
    }

    [Fact]
    public async Task AnAssemblyWithoutDocumentation()
    {
        await CopySampleAsync(xml: false);
        var result = await BuildCachedAsync(StepTestBase.Workflow(Api()));

        Assert.True(result.Succeeded);
        Assert.Contains(Messages, x => x.Level == LogLevel.Warning && x.Message == "'/lib/Contoso.Widgets.dll' has no XML documentation file next to it ('/lib/Contoso.Widgets.xml'): its API is listed without documentation");
        Assert.Null(Type(ApiValue(result), "Contoso.Widgets.Widget")["summary"]);
    }

    [Fact]
    public async Task Errors()
    {
        await WriteBytesAsync("/lib/a.dll", [1, 2, 3]);
        var result = await BuildAsync(StepTestBase.Workflow(Api()));
        Assert.Contains("'/lib/a.dll' is not a .NET assembly", Assert.Single(result.Errors).Message);

        Source = new();
        Site = null;
        await CopySampleAsync();
        await Source.WriteTextFileAsync("/lib/other.xml", "<root/>");
        result = await BuildAsync(StepTestBase.Workflow(Api()));
        Assert.Contains("'/lib/other.xml' is not an XML documentation file: its root element is not <doc>", Assert.Single(result.Errors).Message);

        Source = new();
        Site = null;
        await CopySampleAsync("/lib/net10.0/");
        await CopySampleAsync("/lib/net11.0/");
        result = await BuildAsync(StepTestBase.Workflow(Api()));
        Assert.Contains("the assembly 'Contoso.Widgets' is read from both '/lib/net10.0/Contoso.Widgets.dll' and '/lib/net11.0/Contoso.Widgets.dll': give the step one of them (a target framework)", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public void InvalidOptions()
    {
        var e = Assert.Throws<WorkflowValidationException>(() => WorkflowGraph.Build(StepTestBase.Workflow(Api(StepTestBase.With(("external", NodeValue.Create("x")))))));
        Assert.Equal("option 'external' of step 'api' must be a mapping of namespaces to url patterns", e.Errors.Single().Message);

        e = Assert.Throws<WorkflowValidationException>(() => WorkflowGraph.Build(StepTestBase.Workflow(Api(StepTestBase.With(("pages", NodeValue.Create(true)))))));
        Assert.StartsWith("unknown option 'pages' for step 'api'", e.Errors.Single().Message);
    }
}
