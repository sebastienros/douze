using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Douze.Tests.Tools;

/// <summary>
/// A fake npm registry served on the loopback interface (<c>localhost</c>, as the dev server): the packages it publishes
/// and the requests it received.
/// </summary>
internal sealed class FakeRegistry : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, (int Status, byte[] Body)> _responses = new(StringComparer.Ordinal);

    public FakeRegistry()
    {
        var port = FreePort();
        Url = new Uri($"http://localhost:{port}/registry/");
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    /// <summary>
    /// The url of the registry, to give to the installer.
    /// </summary>
    public Uri Url { get; }

    /// <summary>
    /// The paths and queries of the requests, in order.
    /// </summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            var path = context.Request.RawUrl ?? "";
            Requests.Enqueue(path);

            // A slow response, so that concurrent installs overlap.
            await Task.Delay(50);

            var (status, body) = _responses.TryGetValue(path, out var response) ? response : (404, "not found"u8.ToArray());
            context.Response.StatusCode = status;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
    }

    /// <summary>
    /// Serves the tarball of <paramref name="package"/> <paramref name="version"/> and its document, with
    /// <paramref name="integrity"/> (the tarball's by default). Returns the path of the tarball.
    /// </summary>
    public string Publish(string package, string version, byte[] tarball, string? integrity = null)
    {
        var name = package[(package.LastIndexOf('/') + 1)..];
        var tarballPath = $"/registry/{package}/-/{name}-{version}.tgz";
        _responses[tarballPath] = (200, tarball);
        var document = $$"""{ "name": "{{package}}", "version": "{{version}}", "dist": { "tarball": "{{new Uri(Url, tarballPath["/registry/".Length..]).AbsoluteUri}}", "integrity": "{{integrity ?? Sri(tarball)}}" } }""";
        _responses[$"/registry/{package.Replace("/", "%2F", StringComparison.Ordinal)}/{version}"] = (200, Encoding.UTF8.GetBytes(document));
        return tarballPath;
    }

    /// <summary>
    /// A package tarball with <paramref name="entries"/> (paths and contents).
    /// </summary>
    public static byte[] Tarball(params (string Name, string Content)[] entries)
    {
        using var memory = new MemoryStream();

        using (var gzip = new GZipStream(memory, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Ustar))
        {
            foreach (var (name, content) in entries)
            {
                tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
            }
        }

        return memory.ToArray();
    }

    public static string Sri(byte[] bytes) => "sha512-" + Convert.ToBase64String(SHA512.HashData(bytes));

    public void Dispose()
    {
        _listener.Close();
    }
}
