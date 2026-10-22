using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Douze.FileSystems;

namespace Douze.Workflow.Watch;

/// <summary>
/// The live reload of <c>douze serve</c>: pages connect to <c>/__douze/events</c> (Server-Sent Events), and after each
/// build every page receives <c>{"changed":["/blog/a/","/styles.css"]}</c>, the URLs of the output files the build
/// wrote or deleted (<c>/blog/a/index.html</c> is <c>/blog/a/</c>). <c>livereload.js</c> decides what to reload. A
/// page first receives a <c>hello</c> event with the session id, so that it reloads when it reconnects to a restarted
/// server.
/// </summary>
internal sealed class LiveReload
{
    public const string ScriptPath = "/__douze/livereload.js";
    public const string EventsPath = "/__douze/events";

    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(20);

    private readonly ConcurrentDictionary<long, Channel<byte[]>> _clients = new();
    private long _nextClient;

    public string Session { get; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// The pages connected now.
    /// </summary>
    public int Clients => _clients.Count;

    /// <summary>
    /// The script tag injected in HTML pages.
    /// </summary>
    public static readonly byte[] ScriptTag = Encoding.UTF8.GetBytes($"<script src=\"{ScriptPath}\"></script>");

    /// <summary>
    /// The content of <c>livereload.js</c>.
    /// </summary>
    public static byte[] Script { get; } = ReadScript();

    private static byte[] ReadScript()
    {
        using var stream = typeof(LiveReload).Assembly.GetManifestResourceStream("livereload.js")
            ?? throw new InvalidOperationException("The livereload.js resource is missing");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>
    /// The URL of an output file: <c>/blog/a/</c> for <c>/blog/a/index.html</c>, the path otherwise.
    /// </summary>
    public static string ToUrl(FPath path)
    {
        var name = path.FullName;
        return name.EndsWith("/index.html", StringComparison.Ordinal) ? name[..^"index.html".Length] : name;
    }

    /// <summary>
    /// Tells every connected page about the output files a build changed. Nothing is sent when nothing changed.
    /// </summary>
    public void Notify(IReadOnlyList<FPath> changed)
    {
        if (changed.Count == 0)
        {
            return;
        }

        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("changed");

            foreach (var url in changed.Select(ToUrl).Distinct())
            {
                writer.WriteStringValue(url);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var message = Encoding.UTF8.GetBytes($"data: {Encoding.UTF8.GetString(buffer.ToArray())}\n\n");

        foreach (var client in _clients.Values)
        {
            client.Writer.TryWrite(message);
        }
    }

    /// <summary>
    /// Serves the events of one page until it disconnects or <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task ServeAsync(HttpListenerResponse response, CancellationToken ct)
    {
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.SendChunked = true;

        var id = Interlocked.Increment(ref _nextClient);
        var messages = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        _clients[id] = messages;

        try
        {
            var stream = response.OutputStream;
            await WriteAsync(stream, Encoding.UTF8.GetBytes($"retry: 1000\nevent: hello\ndata: {{\"session\":\"{Session}\"}}\n\n"), ct);

            while (!ct.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(Heartbeat);

                byte[] message;

                try
                {
                    message = await messages.Reader.ReadAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // A comment, so that a page that went away is noticed.
                    message = ": ping\n\n"u8.ToArray();
                }

                await WriteAsync(stream, message, ct);
            }
        }
        catch (Exception e) when (e is IOException or HttpListenerException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // The page went away, or the server stops.
        }
        finally
        {
            _clients.TryRemove(id, out _);

            try
            {
                response.Close();
            }
            catch (Exception e) when (e is IOException or HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }
    }

    private static async Task WriteAsync(Stream stream, byte[] message, CancellationToken ct)
    {
        await stream.WriteAsync(message, ct);
        await stream.FlushAsync(ct);
    }
}
