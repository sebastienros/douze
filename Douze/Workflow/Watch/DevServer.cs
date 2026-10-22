using System.Net;
using Douze.FileSystems;

namespace Douze.Workflow.Watch;

/// <summary>
/// The server of <c>douze serve</c>: serves the output folder on <c>http://localhost:&lt;port&gt;/</c> (GET and HEAD),
/// with the live reload script injected before <c>&lt;/body&gt;</c> in HTML pages and the live reload events on
/// <c>/__douze/events</c> (<see cref="LiveReload"/>). A folder URL serves its <c>index.html</c>, a folder without its
/// final slash redirects to it, a missing file serves <c>/404.html</c> when the site has one. Nothing is cached by the
/// browser: every page asks again after a reload.
/// </summary>
internal sealed class DevServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<string?> _root;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _accepting;

    /// <param name="root">The output folder, read for every request: a workflow file can move it.</param>
    /// <param name="port">The port to listen on.</param>
    /// <param name="reload">The live reload events.</param>
    public DevServer(Func<string?> root, int port, LiveReload reload)
    {
        _root = root;
        Port = port;
        Reload = reload;
        _listener.Prefixes.Add(Url);
    }

    public int Port { get; }

    public LiveReload Reload { get; }

    public string Url => $"http://localhost:{Port}/";

    /// <exception cref="DouzeException">The server cannot listen on the port.</exception>
    public void Start()
    {
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException e)
        {
            throw new DouzeException($"cannot listen on {Url}: {e.Message} (choose another port with --port)");
        }

        _accepting = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // Stopped.
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;

        try
        {
            var request = context.Request;
            var path = request.Url?.AbsolutePath ?? "/";

            if (request.HttpMethod is not ("GET" or "HEAD"))
            {
                response.Headers["Allow"] = "GET, HEAD";
                await SendTextAsync(response, 405, "Method Not Allowed", request.HttpMethod == "HEAD");
                return;
            }

            if (path == LiveReload.EventsPath)
            {
                await Reload.ServeAsync(response, _stopping.Token);
                return;
            }

            if (path == LiveReload.ScriptPath)
            {
                await SendAsync(response, 200, "text/javascript; charset=utf-8", LiveReload.Script, request.HttpMethod == "HEAD");
                return;
            }

            await ServeFileAsync(request, response, Uri.UnescapeDataString(path));
        }
        catch (Exception e) when (e is IOException or HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
            // The browser went away.
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception e) when (e is IOException or HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }
    }

    private async Task ServeFileAsync(HttpListenerRequest request, HttpListenerResponse response, string path)
    {
        var head = request.HttpMethod == "HEAD";

        if (_root() is not { } root || Resolve(root, path) is not { } fullPath)
        {
            await SendTextAsync(response, 400, "Bad Request", head);
            return;
        }

        if (path.EndsWith('/'))
        {
            fullPath = Path.Combine(fullPath, "index.html");
        }
        else if (Directory.Exists(fullPath))
        {
            response.RedirectLocation = path + "/" + request.Url!.Query;
            await SendTextAsync(response, 301, "Moved Permanently", head);
            return;
        }

        var status = 200;

        if (!File.Exists(fullPath))
        {
            var notFound = Path.Combine(root, "404.html");

            if (!File.Exists(notFound))
            {
                await SendTextAsync(response, 404, "Not Found", head);
                return;
            }

            (status, fullPath) = (404, notFound);
        }

        byte[] bytes;

        try
        {
            bytes = await File.ReadAllBytesAsync(fullPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Being written by a build.
            await SendTextAsync(response, 503, "Service Unavailable", head);
            return;
        }

        var contentType = ContentTypes.FromPath(new FPath(fullPath));

        if (contentType == "text/html")
        {
            bytes = InjectScript(bytes);
        }

        await SendAsync(response, status, WithCharset(contentType), bytes, head);
    }

    /// <summary>
    /// The full path of a URL path in <paramref name="root"/>, or null when it is outside of it.
    /// </summary>
    internal static string? Resolve(string root, string path)
    {
        if (path.Contains('\0') || path.Contains('\\'))
        {
            return null;
        }

        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(Path.Combine(root, path.TrimStart('/')));

        return fullPath == root || fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? fullPath : null;
    }

    /// <summary>
    /// Inserts the live reload script before the last <c>&lt;/body&gt;</c>, or at the end of a page without one.
    /// </summary>
    internal static byte[] InjectScript(byte[] html)
    {
        var index = LastIndexOfIgnoreCase(html, "</body"u8);

        if (index < 0)
        {
            index = html.Length;
        }

        var tag = LiveReload.ScriptTag;
        var result = new byte[html.Length + tag.Length];
        html.AsSpan(0, index).CopyTo(result);
        tag.CopyTo(result.AsSpan(index));
        html.AsSpan(index).CopyTo(result.AsSpan(index + tag.Length));
        return result;
    }

    private static int LastIndexOfIgnoreCase(ReadOnlySpan<byte> text, ReadOnlySpan<byte> value)
    {
        for (var i = text.Length - value.Length; i >= 0; i--)
        {
            var match = true;

            for (var j = 0; j < value.Length && match; j++)
            {
                match = (text[i + j] | 0x20) == (value[j] | 0x20);
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    private static string WithCharset(string contentType)
    {
        return contentType.StartsWith("text/", StringComparison.Ordinal) || contentType is "application/json" or "application/xml" or "application/yaml" or "image/svg+xml"
            ? contentType + "; charset=utf-8"
            : contentType;
    }

    private static Task SendTextAsync(HttpListenerResponse response, int status, string text, bool head)
    {
        return SendAsync(response, status, "text/plain; charset=utf-8", System.Text.Encoding.UTF8.GetBytes($"{status} {text}"), head);
    }

    private static async Task SendAsync(HttpListenerResponse response, int status, string contentType, byte[] body, bool head)
    {
        response.StatusCode = status;
        response.ContentType = contentType;
        response.Headers["Cache-Control"] = "no-cache";
        response.ContentLength64 = body.Length;

        if (!head)
        {
            await response.OutputStream.WriteAsync(body);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();

        if (_listener.IsListening)
        {
            _listener.Stop();
        }

        if (_accepting != null)
        {
            await _accepting;
        }

        _listener.Close();
        _stopping.Dispose();
    }
}
