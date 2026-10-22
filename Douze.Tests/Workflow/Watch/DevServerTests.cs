using System.Net;
using System.Net.Sockets;
using System.Text;
using Douze.FileSystems;
using Douze.Workflow.Watch;
using Xunit;

namespace Douze.Tests.Workflow.Watch;

/// <summary>
/// <c>douze serve</c>'s server, on a free port of the loopback, over a temporary output folder.
/// </summary>
public sealed class DevServerTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "douze-tests", Guid.NewGuid().ToString("N"));
    private readonly HttpClient _client = new(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly LiveReload _reload = new();
    private readonly DevServer _server;

    public DevServerTests()
    {
        Directory.CreateDirectory(_root);
        _server = StartServer(_root, _reload);
        _client.BaseAddress = new Uri(_server.Url);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Starts a server on a free port: another process may take the port between the probe and the start.
    /// </summary>
    internal static DevServer StartServer(string root, LiveReload reload)
    {
        for (var attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var server = new DevServer(() => root, port, reload);

            try
            {
                server.Start();
                return server;
            }
            catch (DouzeException) when (attempt < 5)
            {
            }
        }
    }

    private void Write(string path, string content)
    {
        var fullPath = Path.Combine(_root, path.TrimStart('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    [Fact]
    public async Task ServesFilesWithTheirContentType()
    {
        Write("/styles.css", "body { color: red; }");
        Write("/data/a.json", "{}");
        Write("/image.png", "PNG");

        using var css = await _client.GetAsync("/styles.css", Ct);
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("text/css; charset=utf-8", css.Content.Headers.ContentType!.ToString());
        Assert.Equal("body { color: red; }", await css.Content.ReadAsStringAsync(Ct));
        Assert.Equal("no-cache", css.Headers.CacheControl!.ToString());

        using var json = await _client.GetAsync("/data/a.json", Ct);
        Assert.Equal("application/json; charset=utf-8", json.Content.Headers.ContentType!.ToString());

        using var png = await _client.GetAsync("/image.png", Ct);
        Assert.Equal("image/png", png.Content.Headers.ContentType!.ToString());

        using var head = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/styles.css"), Ct);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(20, head.Content.Headers.ContentLength);

        using var post = await _client.PostAsync("/styles.css", new StringContent(""), Ct);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
    }

    [Fact]
    public async Task InjectsTheLiveReloadScriptInHtmlOnly()
    {
        Write("/index.html", "<html><body><p>Home</p></BODY></html>");
        Write("/fragment.html", "<p>no body</p>");
        Write("/page.txt", "</body>");

        var script = "<script src=\"/__douze/livereload.js\"></script>";

        Assert.Equal($"<html><body><p>Home</p>{script}</BODY></html>", await _client.GetStringAsync("/", Ct));
        Assert.Equal($"<p>no body</p>{script}", await _client.GetStringAsync("/fragment.html", Ct));
        Assert.Equal("</body>", await _client.GetStringAsync("/page.txt", Ct));

        using var js = await _client.GetAsync("/__douze/livereload.js", Ct);
        Assert.Equal("text/javascript; charset=utf-8", js.Content.Headers.ContentType!.ToString());
        Assert.Contains("/__douze/events", await js.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task FolderUrlsServeTheirIndexAndMissingFiles404()
    {
        Write("/blog/a/index.html", "<p>a</p>");

        Assert.StartsWith("<p>a</p>", await _client.GetStringAsync("/blog/a/", Ct));

        using var redirect = await _client.GetAsync("/blog/a?x=1", Ct);
        Assert.Equal(HttpStatusCode.MovedPermanently, redirect.StatusCode);
        Assert.Equal("/blog/a/?x=1", redirect.Headers.Location!.OriginalString);

        using var missing = await _client.GetAsync("/nope.html", Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("404 Not Found", await missing.Content.ReadAsStringAsync(Ct));

        using var folderWithoutIndex = await _client.GetAsync("/blog/", Ct);
        Assert.Equal(HttpStatusCode.NotFound, folderWithoutIndex.StatusCode);

        // The site's own 404 page, when it has one.
        Write("/404.html", "<p>lost</p>");
        using var custom = await _client.GetAsync("/nope.html", Ct);
        Assert.Equal(HttpStatusCode.NotFound, custom.StatusCode);
        Assert.StartsWith("<p>lost</p><script", await custom.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task PathsOutsideTheOutputFolderAreRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "site");

        Assert.Equal(Path.Combine(root, "a", "b.html"), DevServer.Resolve(root, "/a/b.html"));
        Assert.Equal(root, DevServer.Resolve(root, "/"));
        Assert.Null(DevServer.Resolve(root, "/../secret.txt"));
        Assert.Null(DevServer.Resolve(root, "/a/../../secret.txt"));
        Assert.Null(DevServer.Resolve(root, "/a\\..\\..\\secret.txt"));
        Assert.Null(DevServer.Resolve(root, "/../site2/x"));

        // Encoded dots reach the server decoded.
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_root)!, "secret.txt"), "secret");
        using var request = new HttpRequestMessage(HttpMethod.Get, _server.Url + "%2e%2e/secret.txt");
        using var response = await _client.SendAsync(request, Ct);
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ConnectedPagesReceiveTheChangedUrls()
    {
        await using var events = await EventStream.OpenAsync(_client, Ct);

        var hello = await events.ReadAsync(Ct);
        Assert.Equal(("hello", $"{{\"session\":\"{_reload.Session}\"}}"), hello);

        await WaitForClientsAsync(1);

        // Nothing changed: nothing is sent.
        _reload.Notify([]);
        _reload.Notify(["/blog/a/index.html", "/index.html", "/styles.css", "/a \"b\".html"]);

        Assert.Equal(("message", "{\"changed\":[\"/blog/a/\",\"/\",\"/styles.css\",\"/a \\u0022b\\u0022.html\"]}"), await events.ReadAsync(Ct));
    }

    private async Task WaitForClientsAsync(int count)
    {
        for (var i = 0; _reload.Clients < count; i++)
        {
            Assert.True(i < 500, "the page did not connect");
            await Task.Delay(10, Ct);
        }
    }

    [Fact]
    public async Task EditingAPageReloadsItsUrlOnly()
    {
        // A session builds a site to the served folder; an edit of the home page changes '/' only.
        var source = new MemoryFileSystem();
        await source.WriteTextFileAsync("/_data/layout.json", "\"layout.html\"");
        await source.WriteTextFileAsync("/_includes/layout.html", "<html><body>{{ content }}</body></html>");
        await source.WriteTextFileAsync("/_content/index.md", "---\ntitle: Home\ndate: 2020-01-01\n---\nHome");
        await source.WriteTextFileAsync("/_content/about.md", "---\ntitle: About\ndate: 2020-01-02\n---\nAbout {{ collections.all | size }}");

        using var session = new TestSession(source);
        await session.BuildSuccessfullyAsync();

        await using var events = await EventStream.OpenAsync(_client, Ct);
        await events.ReadAsync(Ct);
        await WaitForClientsAsync(1);

        await source.WriteTextFileAsync("/_content/index.md", "---\ntitle: Home\ndate: 2020-01-01\n---\nHome, edited");
        var result = await session.BuildSuccessfullyAsync(TestSession.Changes(("/_content/index.md", ChangeKind.Modified)));
        _reload.Notify(result.Changed);

        Assert.Equal(("message", "{\"changed\":[\"/\"]}"), await events.ReadAsync(Ct));
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        _client.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// A Server-Sent Events client: reads the events of <c>/__douze/events</c>, skipping comments.
    /// </summary>
    internal sealed class EventStream : IAsyncDisposable
    {
        private readonly HttpResponseMessage _response;
        private readonly StreamReader _reader;

        private EventStream(HttpResponseMessage response, StreamReader reader)
        {
            _response = response;
            _reader = reader;
        }

        public static async Task<EventStream> OpenAsync(HttpClient client, CancellationToken ct)
        {
            var response = await client.GetAsync(LiveReload.EventsPath, HttpCompletionOption.ResponseHeadersRead, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);

            return new EventStream(response, new StreamReader(await response.Content.ReadAsStreamAsync(ct), Encoding.UTF8));
        }

        /// <summary>
        /// The next event: its type (<c>message</c> when it has none) and its data.
        /// </summary>
        public async Task<(string Event, string Data)> ReadAsync(CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            var type = "message";
            var data = new StringBuilder();

            while (await _reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.Length == 0)
                {
                    if (data.Length > 0)
                    {
                        return (type, data.ToString());
                    }

                    continue;
                }

                if (line.StartsWith(':'))
                {
                    continue;
                }

                var colon = line.IndexOf(':');
                var (field, value) = colon < 0 ? (line, "") : (line[..colon], line[(colon + 1)..].TrimStart(' '));

                switch (field)
                {
                    case "event": type = value; break;
                    case "data": data.Append(value); break;
                }
            }

            throw new EndOfStreamException("the event stream ended");
        }

        public ValueTask DisposeAsync()
        {
            _reader.Dispose();
            _response.Dispose();
            return default;
        }
    }
}
