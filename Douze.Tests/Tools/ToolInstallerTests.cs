using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Douze.Progress;
using Douze.Tools;
using Xunit;

namespace Douze.Tests.Tools;

/// <summary>
/// Installs a fake tool from a fake npm registry served on the loopback interface.
/// </summary>
public sealed class ToolInstallerTests : IDisposable
{
    private const string Platform = "test-x64";
    private const string Package = "@fake/test-x64";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "douze-tests", Guid.NewGuid().ToString("N"), "tools");
    private readonly ConcurrentQueue<string> _requests = new();
    private readonly ConcurrentQueue<(LogLevel, string)> _log = new();
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, (int Status, byte[] Body)> _responses = new(StringComparer.Ordinal);
    private readonly Uri _registry;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ToolInstallerTests()
    {
        var port = FreePort();
        _registry = new Uri($"http://localhost:{port}/registry/");
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

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
            _requests.Enqueue(path);

            // A slow response, so that concurrent installs overlap.
            await Task.Delay(50);

            var (status, body) = _responses.TryGetValue(path, out var response) ? response : (404, "not found"u8.ToArray());
            context.Response.StatusCode = status;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
    }

    /// <summary>
    /// A package tarball: <c>package/bin/tool</c> and <c>package/lib.d.ts</c>.
    /// </summary>
    private static byte[] Tarball(string tool = "#!/bin/sh\necho tool\n")
    {
        using var memory = new MemoryStream();

        using (var gzip = new GZipStream(memory, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Ustar))
        {
            foreach (var (name, content) in new[] { ("package/bin/tool", tool), ("package/lib.d.ts", "declare const x: number;") })
            {
                var entry = new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) };
                tar.WriteEntry(entry);
            }
        }

        return memory.ToArray();
    }

    private static string Sri(byte[] bytes) => "sha512-" + Convert.ToBase64String(SHA512.HashData(bytes));

    private NpmTool Tool(string? knownIntegrity = null) => new(
        "fake",
        "1.0.0",
        new Dictionary<string, string> { [Platform] = Package },
        "package/bin/tool",
        knownIntegrity == null ? new Dictionary<string, string>() : new Dictionary<string, string> { [Platform] = knownIntegrity });

    private ToolInstaller Installer() => new(_root, _registry, (level, message) => _log.Enqueue((level, message)));

    /// <summary>
    /// Serves the tarball of version <paramref name="version"/> and its document, with <paramref name="integrity"/>.
    /// </summary>
    private void Publish(string version, byte[] tarball, string? integrity = null)
    {
        var tarballPath = $"/registry/{Package}/-/test-x64-{version}.tgz";
        _responses[tarballPath] = (200, tarball);
        var document = $$"""{ "name": "{{Package}}", "version": "{{version}}", "dist": { "tarball": "{{new Uri(_registry, tarballPath[10..]).AbsoluteUri}}", "integrity": "{{integrity ?? Sri(tarball)}}" } }""";
        _responses[$"/registry/@fake%2Ftest-x64/{version}"] = (200, Encoding.UTF8.GetBytes(document));
    }

    [Fact]
    public async Task InstallsFromTheRegistryOnceAndReusesTheFolder()
    {
        var tarball = Tarball();
        Publish("2.0.0", tarball);

        var executable = await Installer().InstallAsync(Tool(), "2.0.0", Ct, Platform);

        Assert.Equal(Path.Combine(_root, "fake", "2.0.0", Platform, "package", "bin", "tool"), executable);
        Assert.Equal("#!/bin/sh\necho tool\n", await File.ReadAllTextAsync(executable, Ct));
        Assert.True(File.Exists(Path.Combine(_root, "fake", "2.0.0", Platform, "package", "lib.d.ts")));

        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(executable).HasFlag(UnixFileMode.UserExecute));
        }

        Assert.Equal(["/registry/@fake%2Ftest-x64/2.0.0", "/registry/@fake/test-x64/-/test-x64-2.0.0.tgz"], _requests);
        Assert.Contains(_log, x => x.Item2.Contains("downloading fake 2.0.0", StringComparison.Ordinal));

        // Installed: no request.
        Assert.Equal(executable, await Installer().InstallAsync(Tool(), "2.0.0", Ct, Platform));
        Assert.Equal(2, _requests.Count);
    }

    [Fact]
    public async Task TheDefaultVersionIsVerifiedAgainstTheKnownIntegrity()
    {
        var tarball = Tarball();
        Publish("1.0.0", tarball, integrity: "sha512-AAAA");

        await Installer().InstallAsync(Tool(Sri(tarball)), "1.0.0", Ct, Platform);

        // The registry's document is not read: the tarball url and the integrity are known.
        Assert.Equal(["/registry/@fake/test-x64/-/test-x64-1.0.0.tgz"], _requests);
    }

    [Fact]
    public async Task ADownloadThatDoesNotMatchItsIntegrityFails()
    {
        Publish("2.0.0", Tarball(), integrity: Sri(Tarball("#!/bin/sh\necho other\n")));

        var e = await Assert.ThrowsAsync<DouzeException>(() => Installer().InstallAsync(Tool(), "2.0.0", Ct, Platform));

        Assert.Contains("does not match its integrity", e.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "fake", "2.0.0", Platform)));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "fake", "2.0.0")), x => !x.EndsWith(".lock", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnknownVersionFails()
    {
        var e = await Assert.ThrowsAsync<DouzeException>(() => Installer().InstallAsync(Tool(), "9.9.9", Ct, Platform));

        Assert.Contains("cannot find @fake/test-x64@9.9.9", e.Message);
        Assert.Contains("404", e.Message);
    }

    [Fact]
    public async Task APlatformWithoutPackageFails()
    {
        var e = await Assert.ThrowsAsync<DouzeException>(() => Installer().InstallAsync(Tool(), "1.0.0", Ct, "win-arm64"));

        Assert.Equal($"fake has no package for the platform 'win-arm64' (it has {Platform}): set the path of a fake binary", e.Message);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task ConcurrentInstallsDownloadOnce()
    {
        Publish("2.0.0", Tarball());

        var paths = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Installer().InstallAsync(Tool(), "2.0.0", Ct, Platform), Ct)));

        Assert.Single(paths.Distinct());
        Assert.Single(_requests, x => x.EndsWith(".tgz", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EntriesOutsideThePackageFolderAreRejected()
    {
        using var memory = new MemoryStream();

        using (var gzip = new GZipStream(memory, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Ustar))
        {
            tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "../escape") { DataStream = new MemoryStream("x"u8.ToArray()) });
        }

        Publish("2.0.0", memory.ToArray());

        var e = await Assert.ThrowsAsync<DouzeException>(() => Installer().InstallAsync(Tool(), "2.0.0", Ct, Platform));
        Assert.Contains("outside its folder", e.Message);
    }

    [Fact]
    public void IntegrityPicksTheStrongestHash()
    {
        var bytes = "data"u8.ToArray();
        var (algorithm, hash) = Integrity.Parse($"sha1-{Convert.ToBase64String(SHA1.HashData(bytes))} {Sri(bytes)}");

        Assert.Equal(HashAlgorithmName.SHA512, algorithm);
        Assert.Equal(SHA512.HashData(bytes), hash);
        Assert.Throws<DouzeException>(() => Integrity.Parse("md5-abc"));
    }

    /// <summary>
    /// A NuGet package: <c>tools/net10.0/&lt;platform&gt;/Tool.dll</c>.
    /// </summary>
    private static byte[] NuGetPackage()
    {
        using var memory = new MemoryStream();

        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = zip.CreateEntry($"tools/net10.0/{Platform}/Tool.dll").Open();
            stream.Write("not really a dll"u8);
        }

        return memory.ToArray();
    }

    private static NuGetTool NuGet(string integrity) => new(
        "fake-nuget",
        "1.0.0-Beta.1",
        new Dictionary<string, string> { [Platform] = "Fake.Tool." + Platform },
        "tools/net10.0/{platform}/Tool.dll",
        new Dictionary<string, string> { [Platform] = integrity });

    [Fact]
    public async Task InstallsNuGetPackages()
    {
        var package = NuGetPackage();
        _responses[$"/registry/fake.tool.{Platform}/1.0.0-beta.1/fake.tool.{Platform}.1.0.0-beta.1.nupkg"] = (200, package);

        var path = await Installer().InstallAsync(NuGet(Sri(package)), "1.0.0-Beta.1", Ct, Platform);

        Assert.Equal(Path.Combine(_root, "fake-nuget", "1.0.0-Beta.1", Platform, "tools/net10.0/test-x64/Tool.dll"), path);
        Assert.Equal("not really a dll", await File.ReadAllTextAsync(path, Ct));

        // Installed once.
        await Installer().InstallAsync(NuGet(Sri(package)), "1.0.0-Beta.1", Ct, Platform);
        Assert.Single(_requests);
    }

    [Fact]
    public async Task NuGetPackagesAreVerified()
    {
        _responses[$"/registry/fake.tool.{Platform}/1.0.0-beta.1/fake.tool.{Platform}.1.0.0-beta.1.nupkg"] = (200, NuGetPackage());

        var e = await Assert.ThrowsAsync<DouzeException>(() => Installer().InstallAsync(NuGet(Sri("other"u8.ToArray())), "1.0.0-Beta.1", Ct, Platform));

        Assert.Contains("does not match its integrity", e.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "fake-nuget", "1.0.0-Beta.1", Platform)));

        // Another version than the known one: its integrity is only known on nuget.org.
        e = await Assert.ThrowsAsync<DouzeException>(() => Installer().InstallAsync(NuGet(Sri("other"u8.ToArray())), "2.0.0", Ct, Platform));
        Assert.Contains("only known for the default version", e.Message);
    }

    [Fact]
    public void CurrentPlatform()
    {
        var platform = NpmTool.CurrentPlatform;

        Assert.Matches("^(osx|linux|linux-musl|win|[a-z]+)-[a-z0-9]+$", platform);
    }

    public void Dispose()
    {
        _listener.Close();

        try
        {
            Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
