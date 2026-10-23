using System.Net;
using System.Net.Sockets;
using Douze.FileSystems;
using Douze.Nodes;
using Douze.Progress;
using Douze.Tests.Workflow.Cache;
using Douze.Workflow;
using Douze.Workflow.Steps;
using Xunit;

namespace Douze.Tests.Workflow.Steps;

public class CheckLinksStepTests : CacheTestBase
{
    private static WorkflowDefinition Check(NodeObject? with = null) => StepTestBase.Workflow(
        StepTestBase.Step("pages", "frontmatter", inputs: ["**"]),
        StepTestBase.Step("links", "links", needs: ["pages"]),
        StepTestBase.Step("check", "check-links", needs: ["links"], with: with),
        StepTestBase.Step("write", "write", needs: ["links"]));

    private async Task<string[]> ErrorsAsync(WorkflowDefinition workflow)
    {
        var result = await BuildAsync(workflow, Options());
        Assert.False(result.Succeeded);
        return [.. result.Errors.SelectMany(x => x.Message.Split(Environment.NewLine))];
    }

    private string[] Warnings() => [.. Messages.Where(x => x.Level == LogLevel.Warning).Select(x => x.Message)];

    private async Task SiteAsync()
    {
        await Source.WriteTextFileAsync("/index.html", "<h1 id=\"top-title\">Home</h1>");
        await Source.WriteTextFileAsync("/docs/index.html", "<h2 id=\"intro\">Intro</h2>");
        await Source.WriteTextFileAsync("/docs/guide/index.html", "<h2 id=\"setup\">Setup</h2>\n<h2 id=\"caf%C3%A9\">x</h2><h2 id=\"café\">x</h2>");
        await Source.WriteTextFileAsync("/docs/page.html", "<p id=\"here\"></p>");
        await Source.WriteTextFileAsync("/img/a.png", "png");
        await Source.WriteTextFileAsync("/files/My File.pdf", "pdf");
    }

    [Fact]
    public async Task ValidLinksResolve()
    {
        await SiteAsync();
        await Source.WriteTextFileAsync("/docs/guide/links.html", """
            <a href="/">root</a> <a href="/index.html">index</a> <a href="../">parent</a> <a href="./">self folder</a>
            <a href="/docs/guide/#setup">abs</a> <a href="../guide/?tab=2#setup">query</a> <a href="../../docs/page.html#here">dotdot</a>
            <a href="#local">local</a> <a href="#">top</a> <a href="#top">top</a> <a href="?q=1">query only</a> <a id="local"></a>
            <img src="../../img/a.png" srcset="/img/a.png 2x"> <a href="/files/My%20File.pdf#page=2">pdf</a> <a href="/docs/guide/#caf%C3%A9">encoded</a>
            <a href="mailto:a@b.c">mail</a> <a href="tel:1">tel</a> <a href="javascript:void(0)">js</a> <img src="data:image/png;base64,AA==">
            <a href="https://localhost:1/nothing">external</a> <a href="//localhost:1/x">external</a>
            <a href="/docs/guide/#:~:text=Setup">text fragment</a>
            """);

        await BuildCachedAsync(Check());

        Assert.Empty(Warnings());
    }

    [Fact]
    public async Task BrokenLinksAndMissingAnchorsAreErrorsInOrder()
    {
        await SiteAsync();
        await Source.WriteTextFileAsync("/docs/b.html", "<a href=\"/docs/guide/#nope\">x</a>\n<a href=\"/missing/\">x</a>\n<a href=\"#gone\">x</a>");
        await Source.WriteTextFileAsync("/docs/a.html", "\n\n<img src=\"/img/b.png\"><a href=\"/docs\">x</a> <a href=\"../../../x\">x</a> <a href=\"/docs/page\">x</a>");

        var errors = await ErrorsAsync(Check());

        Assert.Equal(
            [
                "/docs/a.html: broken link '../../../x' (line 3)",
                "/docs/a.html: broken link '/docs' (line 3)",
                "/docs/a.html: broken link '/docs/page' (line 3)",
                "/docs/a.html: broken link '/img/b.png' (line 3)",
                "/docs/b.html: missing anchor '#nope' in '/docs/guide/' (line 1)",
                "/docs/b.html: broken link '/missing/' (line 2)",
                "/docs/b.html: missing anchor '#gone' (line 3)",
            ],
            errors);
    }

    [Fact]
    public async Task ExtensionlessLinks()
    {
        await SiteAsync();
        await Source.WriteTextFileAsync("/docs/a.html", "<a href=\"/docs\">x</a> <a href=\"/docs/page#here\">x</a> <a href=\"/docs/none\">x</a>");

        var errors = await ErrorsAsync(Check(StepTestBase.With(("extensionless", NodeValue.Create(true)))));

        Assert.Equal(["/docs/a.html: broken link '/docs/none' (line 1)"], errors);
    }

    [Fact]
    public async Task AnchorsCanBeLeftUnchecked()
    {
        await SiteAsync();
        await Source.WriteTextFileAsync("/docs/a.html", "<a href=\"/docs/#nope\">x</a>");

        await BuildCachedAsync(Check(StepTestBase.With(("anchors", NodeValue.Create(false)))));
    }

    [Fact]
    public async Task IgnoreAndSkipGlobs()
    {
        await SiteAsync();
        await Source.WriteTextFileAsync("/docs/a.html", "<a href=\"/reference/api/x/\">x</a> <a href=\"../i18n\">x</a> <a href=\"/other\">x</a>");
        await Source.WriteTextFileAsync("/legacy/old.html", "<a href=\"/gone/\">x</a>");

        var errors = await ErrorsAsync(Check(StepTestBase.With(
            ("ignore", new NodeArray(NodeValue.Create("/reference/api/**"), NodeValue.Create("i18n/**"))),
            ("skip", NodeValue.Create("legacy/**")))));

        Assert.Equal(["/docs/a.html: broken link '/other' (line 1)"], errors);
    }

    [Fact]
    public async Task WarnReportsWarningsAgainWhenReplayed()
    {
        await SiteAsync();
        await Source.WriteTextFileAsync("/docs/a.html", "<a href=\"/missing/\">x</a>");
        var workflow = Check(StepTestBase.With(("errors", NodeValue.Create("warn"))));

        await BuildCachedAsync(workflow);
        Assert.Equal(["/docs/a.html: broken link '/missing/' (line 1)"], Warnings());

        Messages.Clear();
        var result = await BuildCachedAsync(workflow);

        Assert.Empty(Executed(result, "check"));
        Assert.Equal(["/docs/a.html: broken link '/missing/' (line 1)"], Warnings());
    }

    [Fact]
    public async Task ABodyEditThatChangesNoLinkReplaysTheCheck()
    {
        await SiteAsync();
        await Source.WriteTextFileAsync("/docs/a.html", "<p>Hello</p> <a href=\"/docs/#intro\">x</a>");
        await BuildCachedAsync(Check());

        // The same date: page.date is metadata, a new one runs the check again.
        var date = (await Source.GetFileMetadataAsync("/docs/a.html")).LastWriteTimeUtc;
        await Source.WriteTextFileAsync("/docs/a.html", "<p>Hello, world</p> <a href=\"/docs/#intro\">x</a>");
        Source.SetLastWriteTimeUtc("/docs/a.html", date);
        var result = await BuildCachedAsync(Check());

        Assert.Equal(["/docs/a.html"], Executed(result, "links"));
        Assert.Empty(Executed(result, "check"));

        // A link that changes runs it again.
        await Source.WriteTextFileAsync("/docs/a.html", "<p>Hello, world</p> <a href=\"/docs/#nope\">x</a>");
        Assert.Equal(["/docs/a.html: missing anchor '#nope' in '/docs/' (line 1)"], await ErrorsAsync(Check()));
    }

    [Fact]
    public async Task ExternalLinksAreWarningsWhenChecked()
    {
        using var server = new LocalServer();
        await SiteAsync();
        await Source.WriteTextFileAsync("/docs/a.html", $"""
            <a href="{server.Url}ok">x</a> <a href="{server.Url}missing#frag">x</a>
            <a href="{server.Url}missing">again</a> <a href="{server.Url}ignored/x">x</a>
            """);

        // Off by default.
        await BuildCachedAsync(Check());
        Assert.Empty(Warnings());
        Assert.Equal(0, server.Requests);

        await BuildCachedAsync(Check(StepTestBase.With(("external", NodeValue.Create(true)), ("ignore", NodeValue.Create($"{server.Url}ignored/")))));

        Assert.Equal(
            [$"/docs/a.html: external link '{server.Url}missing#frag' (line 1): 404 Not Found", $"/docs/a.html: external link '{server.Url}missing' (line 2): 404 Not Found"],
            Warnings());

        // HEAD then GET for the missing url, once for both links; HEAD for the other.
        Assert.Equal(3, server.Requests);
    }

    [Fact]
    public void InvalidOptionsAreWorkflowErrors()
    {
        static void Create(params (string Key, Node? Value)[] options) => _ = new CheckLinksStep(StepTestBase.Step("check", "check-links", with: StepTestBase.With(options)));

        Assert.Contains("must be 'fail' or 'warn'", Assert.Throws<WorkflowValidationException>(() => Create(("errors", NodeValue.Create("ignore")))).Message, StringComparison.Ordinal);
        Assert.Throws<WorkflowValidationException>(() => Create(("warn", NodeValue.Create(true))));
        Assert.Throws<WorkflowValidationException>(() => Create(("ignore", NodeValue.Create("a/[b"))));
    }

    [Theory]
    [InlineData("/docs/a.html", "b.html", "/docs/b.html")]
    [InlineData("/docs/a/index.html", "../b/", "/docs/b/")]
    [InlineData("/docs/a/index.html", "./", "/docs/a/")]
    [InlineData("/docs/a/index.html", "..", "/docs/")]
    [InlineData("/index.html", "/", "/")]
    [InlineData("/index.html", "a/./b//c", "/a/b/c")]
    [InlineData("/index.html", "../a", null)]
    public void Resolve(string page, string link, string? expected)
    {
        Assert.Equal(expected, CheckLinksStep.Resolve(page, link));
    }

    /// <summary>
    /// An HTTP server on a free port of the loopback: <c>/ok</c> answers 200, the rest 404.
    /// </summary>
    private sealed class LocalServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private int _requests;

        public LocalServer()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            Url = $"http://localhost:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = ServeAsync();
        }

        public string Url { get; }

        public int Requests => _requests;

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;

                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                Interlocked.Increment(ref _requests);
                context.Response.StatusCode = context.Request.Url!.AbsolutePath == "/ok" ? 200 : 404;
                context.Response.Close();
            }
        }

        public void Dispose() => _listener.Close();
    }
}
