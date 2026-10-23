using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
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
        Assert.Contains("/__douze/socket", await js.Content.ReadAsStringAsync(Ct));
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
        await using var events = await PageSocket.OpenAsync(_server.Url, Ct);

        var hello = await events.ReadAsync(Ct);
        Assert.Equal($"{{\"type\":\"hello\",\"session\":\"{_reload.Session}\"}}", hello);

        await WaitForClientsAsync(1);

        // Nothing changed: nothing is sent.
        _reload.Notify([]);
        _reload.Notify(["/blog/a/index.html", "/index.html", "/styles.css", "/a \"b\".html"]);

        Assert.Equal("{\"type\":\"changed\",\"changed\":[\"/blog/a/\",\"/\",\"/styles.css\",\"/a \\u0022b\\u0022.html\"]}", await events.ReadAsync(Ct));
    }

    [Fact]
    public async Task OpenPagesDoNotStallOtherRequests()
    {
        // Browsers open at most 6 HTTP/1.1 connections per host: pages that held one each (an event stream) stalled every
        // other request once 6 were open. Sockets don't hold one, and files are served while 8 pages are connected.
        Write("/index.html", "<html><body>Home</body></html>");

        var pages = new List<PageSocket>();

        try
        {
            for (var i = 0; i < 8; i++)
            {
                var socket = await PageSocket.OpenAsync(_server.Url, Ct);
                pages.Add(socket);
                await socket.ReadAsync(Ct);
            }

            await WaitForClientsAsync(8);

            using var browser = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 6 }) { BaseAddress = new Uri(_server.Url), Timeout = TimeSpan.FromSeconds(5) };
            var requests = Enumerable.Range(0, 12).Select(_ => browser.GetStringAsync("/", Ct));
            Assert.All(await Task.WhenAll(requests), body => Assert.Contains("Home", body));
        }
        finally
        {
            foreach (var page in pages)
            {
                await page.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task APageThatGoesAwayIsForgottenAtOnce()
    {
        var page = await PageSocket.OpenAsync(_server.Url, Ct);
        await page.ReadAsync(Ct);
        await WaitForClientsAsync(1);

        await page.DisposeAsync();

        // Well before the keep-alive interval.
        for (var i = 0; _reload.Clients > 0; i++)
        {
            Assert.True(i < 200, "the server did not notice that the page closed its socket");
            await Task.Delay(10, Ct);
        }
    }

    [Fact]
    public async Task TheSocketPathRejectsPlainRequests()
    {
        using var response = await _client.GetAsync(LiveReload.SocketPath, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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

        await using var events = await PageSocket.OpenAsync(_server.Url, Ct);
        await events.ReadAsync(Ct);
        await WaitForClientsAsync(1);

        await source.WriteTextFileAsync("/_content/index.md", "---\ntitle: Home\ndate: 2020-01-01\n---\nHome, edited");
        var result = await session.BuildSuccessfullyAsync(TestSession.Changes(("/_content/index.md", ChangeKind.Modified)));
        _reload.Notify(result.Changed);

        Assert.Equal("{\"type\":\"changed\",\"changed\":[\"/\"]}", await events.ReadAsync(Ct));
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
    /// What <c>livereload.js</c> does: a WebSocket to <c>/__douze/socket</c>, reading the server's messages.
    /// </summary>
    internal sealed class PageSocket : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket;

        private PageSocket(ClientWebSocket socket)
        {
            _socket = socket;
        }

        public static async Task<PageSocket> OpenAsync(string serverUrl, CancellationToken ct)
        {
            var socket = new ClientWebSocket();
            var url = new Uri(new Uri(serverUrl.Replace("http://", "ws://", StringComparison.Ordinal)), LiveReload.SocketPath);
            await socket.ConnectAsync(url, ct);
            return new PageSocket(socket);
        }

        /// <summary>
        /// The next message, as text.
        /// </summary>
        public async Task<string> ReadAsync(CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            var buffer = new byte[4096];
            using var message = new MemoryStream();

            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer, timeout.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new EndOfStreamException("the server closed the socket");
                }

                message.Write(buffer, 0, result.Count);

                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(message.ToArray());
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
            }

            _socket.Dispose();
        }
    }
}
