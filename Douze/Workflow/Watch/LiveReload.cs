using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Douze.FileSystems;

namespace Douze.Workflow.Watch;

/// <summary>
/// The live reload of <c>douze serve</c>: pages connect a WebSocket to <c>/__douze/socket</c>, and after each build
/// every page receives <c>{"type":"changed","changed":["/blog/a/","/styles.css"]}</c>, the URLs of the output files the
/// build wrote or deleted (<c>/blog/a/index.html</c> is <c>/blog/a/</c>). <c>livereload.js</c> decides what to reload.
/// A page first receives <c>{"type":"hello","session":"..."}</c>, so that it reloads when it reconnects to a restarted
/// server.
/// </summary>
/// <remarks>
/// A WebSocket rather than Server-Sent Events: an event stream is an HTTP/1.1 response that never ends, and browsers
/// open at most 6 connections per host, so 6 open pages (or pages being left whose stream is not closed yet) stalled
/// every other request to the server. WebSockets don't count against that limit, and a page that goes away closes its
/// socket, which ends its session at once.
/// </remarks>
internal sealed class LiveReload
{
    public const string ScriptPath = "/__douze/livereload.js";
    public const string SocketPath = "/__douze/socket";

    /// <summary>
    /// The interval of the pings that find connections dropped without a close (a sleeping laptop).
    /// </summary>
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(10);

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
            writer.WriteString("type", "changed");
            writer.WriteStartArray("changed");

            foreach (var url in changed.Select(ToUrl).Distinct())
            {
                writer.WriteStringValue(url);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var message = buffer.ToArray();

        foreach (var client in _clients.Values)
        {
            client.Writer.TryWrite(message);
        }
    }

    /// <summary>
    /// Serves one page over its WebSocket until it disconnects or <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task ServeAsync(WebSocket socket, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextClient);
        var messages = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        _clients[id] = messages;

        // A page that goes away closes its socket: the receive loop sees it at once and ends the session.
        using var closed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receiving = ReceiveUntilClosedAsync(socket, closed);

        try
        {
            await SendAsync(socket, Encoding.UTF8.GetBytes($"{{\"type\":\"hello\",\"session\":\"{Session}\"}}"), closed.Token);

            await foreach (var message in messages.Reader.ReadAllAsync(closed.Token))
            {
                await SendAsync(socket, message, closed.Token);
            }
        }
        catch (Exception e) when (e is WebSocketException or IOException or HttpListenerException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // The page went away, or the server stops.
        }
        finally
        {
            _clients.TryRemove(id, out _);
            await closed.CancelAsync();
            await receiving;

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
                }
                catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
                {
                }
            }
        }
    }

    private static async Task ReceiveUntilClosedAsync(WebSocket socket, CancellationTokenSource closed)
    {
        var buffer = new byte[256];

        try
        {
            while (!closed.IsCancellationRequested)
            {
                // Pages send nothing: anything but a close is ignored.
                var result = await socket.ReceiveAsync(buffer, closed.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or IOException or HttpListenerException or ObjectDisposedException or OperationCanceledException)
        {
        }

        await closed.CancelAsync();
    }

    private static Task SendAsync(WebSocket socket, byte[] message, CancellationToken ct)
        => socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, ct);
}
