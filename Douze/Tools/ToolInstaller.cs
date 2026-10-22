using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Douze.Progress;
using Douze.Tracing;
using Douze.Workflow.Commands;

namespace Douze.Tools;

/// <summary>
/// Installs tools published as npm packages (<see cref="NpmTool"/>) or NuGet packages (<see cref="NuGetTool"/>) into a
/// tools folder, once: <c>&lt;tools&gt;/&lt;name&gt;/&lt;version&gt;/&lt;platform&gt;/</c> holds the content of the
/// package (an npm tarball, a <c>.nupkg</c> zip); a tool built from Go source (<see cref="GoTool"/>) is built with
/// <c>go install</c> into <c>&lt;tools&gt;/&lt;name&gt;/&lt;version&gt;/</c>. The package is downloaded from the
/// registry, verified against its integrity (the one Douze knows for the default version, the registry's otherwise) and
/// extracted; later builds reuse the folder without any request, so a build that has its tools works offline.
/// Concurrent installs of the same tool, from threads or processes, download it once (a lock file next to the folder).
/// </summary>
internal sealed class ToolInstaller
{
    /// <summary>
    /// The npm registry.
    /// </summary>
    public static readonly Uri DefaultRegistry = new("https://registry.npmjs.org/");

    /// <summary>
    /// The package content (flat container) url of nuget.org.
    /// </summary>
    public static readonly Uri DefaultNuGetRegistry = new("https://api.nuget.org/v3-flatcontainer/");

    // Where the integrity of the versions of nuget.org packages that Douze does not know is read: the catalog entry of
    // a version, from its registration leaf, has its packageHash.
    private static readonly Uri NuGetRegistrations = new("https://api.nuget.org/v3/registration5-gz-semver2/");

    private const string Marker = ".douze-tool";

    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromMinutes(10) };

    private readonly string _root;
    private readonly Uri _registry;
    private readonly bool _customRegistry;
    private readonly HttpClient _http;
    private readonly Action<LogLevel, string> _log;

    /// <param name="root">The tools folder.</param>
    /// <param name="registry">The registry (a mirror, a test server): of npm, <see cref="DefaultRegistry"/> when null,
    /// or the package content url of a NuGet feed, <see cref="DefaultNuGetRegistry"/> when null.</param>
    /// <param name="log">Where the downloads are reported.</param>
    /// <param name="http">The HTTP client; a shared one when null.</param>
    public ToolInstaller(string root, Uri? registry, Action<LogLevel, string> log, HttpClient? http = null)
    {
        _root = root;
        _registry = registry ?? DefaultRegistry;
        _customRegistry = registry != null;
        _http = http ?? SharedClient;
        _log = log;

        if (!_registry.AbsoluteUri.EndsWith('/'))
        {
            _registry = new Uri(_registry.AbsoluteUri + "/");
        }
    }

    /// <summary>
    /// How long an install waits for another one of the same tool to finish. Settable for tests.
    /// </summary>
    internal static TimeSpan LockTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The folder <paramref name="tool"/> <paramref name="version"/> is installed in for <paramref name="platform"/>.
    /// </summary>
    public string GetFolder(NpmTool tool, string version, string platform) => Path.Combine(_root, tool.Name, version, platform);

    /// <summary>
    /// The folder <paramref name="tool"/> <paramref name="version"/> is installed in for <paramref name="platform"/>.
    /// </summary>
    public string GetFolder(NuGetTool tool, string version, string platform) => Path.Combine(_root, tool.Name, version, platform);

    /// <summary>
    /// The folder <paramref name="tool"/> <paramref name="version"/> is built into.
    /// </summary>
    public string GetFolder(GoTool tool, string version) => Path.Combine(_root, tool.Name, version);

    /// <summary>
    /// The path of the binary of <paramref name="tool"/> <paramref name="version"/> when it is installed for the current
    /// platform (or <paramref name="platform"/>), null otherwise. Sends no request.
    /// </summary>
    public string? FindInstalled(NpmTool tool, string version, string? platform = null)
    {
        platform ??= NpmTool.CurrentPlatform;

        return tool.Packages.ContainsKey(platform) && IsVersion(version) && File.Exists(Path.Combine(GetFolder(tool, version, platform), Marker))
            ? GetExecutable(tool, version, platform)
            : null;
    }

    /// <summary>
    /// The path of the entry point of <paramref name="tool"/> <paramref name="version"/> when it is installed for the
    /// current platform (or <paramref name="platform"/>), null otherwise. Sends no request.
    /// </summary>
    public string? FindInstalled(NuGetTool tool, string version, string? platform = null)
    {
        platform ??= NpmTool.CurrentPlatform;

        return tool.Packages.ContainsKey(platform) && IsVersion(version) && File.Exists(Path.Combine(GetFolder(tool, version, platform), Marker))
            ? Path.Combine(GetFolder(tool, version, platform), GetExecutable(tool, platform))
            : null;
    }

    /// <summary>
    /// The path of the binary of <paramref name="tool"/> <paramref name="version"/> when it is built, null otherwise.
    /// Runs nothing.
    /// </summary>
    public string? FindInstalled(GoTool tool, string version)
    {
        return IsGoVersion(version) && File.Exists(Path.Combine(GetFolder(tool, version), Marker))
            ? Path.Combine(GetFolder(tool, version), GetExecutable(tool))
            : null;
    }

    private static bool IsVersion(string version)
    {
        return version.Length > 0 && !version.Contains('/', StringComparison.Ordinal) && !version.Contains('\\', StringComparison.Ordinal) && !version.Contains("..", StringComparison.Ordinal);
    }

    private static bool IsGoVersion(string version)
    {
        return version.Length > 0 && version.IndexOfAny(['/', '\\', '@', ' ']) < 0 && !version.Contains("..", StringComparison.Ordinal);
    }

    private string GetExecutable(NpmTool tool, string version, string platform) => Path.Combine(GetFolder(tool, version, platform), GetExecutable(tool, platform));

    // The paths in a package use '/': they are made native, so that the path of a tool has one separator on Windows.
    private static string GetExecutable(NpmTool tool, string platform) => ToNative(tool.Executable) + (platform.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : "");

    private static string GetExecutable(NuGetTool tool, string platform) => ToNative(tool.Executable.Replace("{platform}", platform, StringComparison.Ordinal));

    private static string ToNative(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    private static string GetExecutable(GoTool tool) => tool.Executable + (OperatingSystem.IsWindows() ? ".exe" : "");

    /// <summary>
    /// Returns the path of the binary of <paramref name="tool"/> <paramref name="version"/> for the current platform
    /// (or <paramref name="platform"/>), installing it first when needed.
    /// </summary>
    /// <exception cref="DouzeException">The tool has no package for the platform, or the download, the verification or
    /// the extraction failed.</exception>
    public async Task<string> InstallAsync(NpmTool tool, string version, CancellationToken ct, string? platform = null)
    {
        platform ??= NpmTool.CurrentPlatform;

        if (!tool.Packages.TryGetValue(platform, out var package))
        {
            throw new DouzeException($"{tool.Name} has no package for the platform '{platform}' (it has {string.Join(", ", tool.Packages.Keys)}): set the path of a {tool.Name} binary");
        }

        if (!IsVersion(version))
        {
            throw new DouzeException($"'{version}' is not a version of {tool.Name}");
        }

        await InstallPackageAsync(tool.Name, version, platform, package, GetExecutable(tool, platform), isZip: false, async () =>
            tool.DefaultVersion == version && tool.KnownIntegrity.TryGetValue(platform, out var known)
                ? (TarballUrl(package, version), known)
                : await ResolveAsync(package, version, ct), ct);
        return GetExecutable(tool, version, platform);
    }

    /// <summary>
    /// Returns the path of the entry point of <paramref name="tool"/> <paramref name="version"/> for the current
    /// platform (or <paramref name="platform"/>), installing it first when needed.
    /// </summary>
    /// <exception cref="DouzeException">The tool has no package for the platform, or the download, the verification or
    /// the extraction failed.</exception>
    public async Task<string> InstallAsync(NuGetTool tool, string version, CancellationToken ct, string? platform = null)
    {
        platform ??= NpmTool.CurrentPlatform;

        if (!tool.Packages.TryGetValue(platform, out var package))
        {
            throw new DouzeException($"{tool.Name} has no package for the platform '{platform}' (it has {string.Join(", ", tool.Packages.Keys)}): set the path of {tool.Name}");
        }

        if (!IsVersion(version))
        {
            throw new DouzeException($"'{version}' is not a version of {tool.Name}");
        }

        var registry = _customRegistry ? _registry : DefaultNuGetRegistry;
        var id = package.ToLowerInvariant();
        var lower = version.ToLowerInvariant();
        var url = new Uri(registry, $"{id}/{lower}/{id}.{lower}.nupkg").AbsoluteUri;

        var executable = GetExecutable(tool, platform);
        var folder = await InstallPackageAsync(tool.Name, version, platform, package, executable, isZip: true, async () =>
            tool.DefaultVersion == version && tool.KnownIntegrity.TryGetValue(platform, out var known)
                ? (url, known)
                : (url, await ResolveNuGetIntegrityAsync(package, version, ct)), ct);
        return Path.Combine(folder, executable);
    }

    /// <summary>
    /// Returns the path of the binary of <paramref name="tool"/> <paramref name="version"/>, building it first when
    /// needed with <c>go install</c> (the <c>go</c> executable <paramref name="go"/>) into
    /// <c>&lt;tools&gt;/&lt;name&gt;/&lt;version&gt;/</c>. The module proxy and the checksum database of the Go
    /// environment verify what it downloads.
    /// </summary>
    /// <exception cref="DouzeException">The version is invalid, or <c>go install</c> failed.</exception>
    public async Task<string> InstallAsync(GoTool tool, string version, string go, CancellationToken ct)
    {
        if (!IsGoVersion(version))
        {
            throw new DouzeException($"'{version}' is not a version of {tool.Name}");
        }

        var folder = GetFolder(tool, version);
        var executable = GetExecutable(tool);

        if (File.Exists(Path.Combine(folder, Marker)))
        {
            return Path.Combine(folder, executable);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
        using var lockFile = await LockAsync(folder + ".lock", tool.Name, ct);

        // Installed by another build while this one waited.
        if (File.Exists(Path.Combine(folder, Marker)))
        {
            return Path.Combine(folder, executable);
        }

        _log(LogLevel.Info, $"installing {tool.Name} {version} with go install {tool.Module}@{version}");

        var temporary = Path.Combine(Path.GetDirectoryName(folder)!, $".{version}.{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);

        try
        {
            var info = new ProcessStartInfo(go)
            {
                ArgumentList = { "install", $"{tool.Module}@{version}" },
                WorkingDirectory = temporary,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            info.Environment["GOBIN"] = temporary;
            info.Environment["GOWORK"] = "off";

            // Flags for the site's own module (-mod=vendor) break an install outside a module.
            info.Environment.Remove("GOFLAGS");

            var result = await ProcessRunner.RunAsync(info, null, captureOutput: true, null, LockTimeout, NullTraceRecorder.Instance, tool.Name, $" (go install {tool.Module}@{version})", ct);

            if (result.ExitCode != 0)
            {
                throw new DouzeException($"cannot install {tool.Name} {version} with go install: {ProcessRunner.Tail(System.Text.Encoding.UTF8.GetString(result.Stdout.Span) + Environment.NewLine + result.Stderr, 10)}");
            }

            if (!File.Exists(Path.Combine(temporary, executable)))
            {
                throw new DouzeException($"go install {tool.Module}@{version} wrote no '{executable}'");
            }

            await File.WriteAllTextAsync(Path.Combine(temporary, Marker), $"{tool.Module}@{version}\n", ct);

            if (Directory.Exists(folder))
            {
                // A previous install that stopped before its marker.
                Directory.Delete(folder, recursive: true);
            }

            Directory.Move(temporary, folder);
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                try
                {
                    Directory.Delete(temporary, recursive: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        _log(LogLevel.Info, $"installed {tool.Name} {version} in {folder}");
        return Path.Combine(folder, executable);
    }

    /// <summary>
    /// Installs a package into <c>&lt;tools&gt;/&lt;name&gt;/&lt;version&gt;/&lt;platform&gt;/</c> unless it is there,
    /// and returns that folder. The package must have <c>executable</c> (relative to its folder), which is made
    /// executable on Unix for an npm package.
    /// </summary>
    private async Task<string> InstallPackageAsync(string name, string version, string platform, string package, string executable, bool isZip, Func<Task<(string Url, string Integrity)>> resolve, CancellationToken ct)
    {
        var folder = Path.Combine(_root, name, version, platform);

        if (File.Exists(Path.Combine(folder, Marker)))
        {
            return folder;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
        using var lockFile = await LockAsync(folder + ".lock", name, ct);

        // Installed by another build while this one waited.
        if (File.Exists(Path.Combine(folder, Marker)))
        {
            return folder;
        }

        var (url, integrity) = await resolve();

        _log(LogLevel.Info, $"downloading {name} {version} ({package}) from {url}");

        var temporary = Path.Combine(Path.GetDirectoryName(folder)!, $".{platform}.{Guid.NewGuid():N}");
        var archive = temporary + (isZip ? ".zip" : ".tgz");

        try
        {
            await DownloadAsync(url, archive, integrity, ct);

            if (isZip)
            {
                ExtractZip(archive, temporary);
            }
            else
            {
                Extract(archive, temporary);
            }

            if (!File.Exists(Path.Combine(temporary, executable)))
            {
                throw new DouzeException($"the package {package}@{version} has no '{executable}'");
            }

            if (!isZip && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(Path.Combine(temporary, executable),
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            await File.WriteAllTextAsync(Path.Combine(temporary, Marker), $"{package}@{version} {integrity}\n", ct);

            if (Directory.Exists(folder))
            {
                // A previous install that stopped before its marker.
                Directory.Delete(folder, recursive: true);
            }

            Directory.Move(temporary, folder);
        }
        finally
        {
            TryDelete(archive);

            if (Directory.Exists(temporary))
            {
                try
                {
                    Directory.Delete(temporary, recursive: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        _log(LogLevel.Info, $"installed {name} {version} in {folder}");
        return folder;
    }

    /// <summary>
    /// The integrity of a version of a nuget.org package that Douze does not know: the SHA-512 of the catalog entry of
    /// the version.
    /// </summary>
    private async Task<string> ResolveNuGetIntegrityAsync(string package, string version, CancellationToken ct)
    {
        if (_customRegistry)
        {
            throw new DouzeException($"the integrity of {package} {version} is only known for the default version on a registry other than nuget.org: use the default version, or set the path of the tool");
        }

        var leaf = new Uri(NuGetRegistrations, $"{package.ToLowerInvariant()}/{version.ToLowerInvariant()}.json");

        try
        {
            using var response = await _http.GetAsync(leaf, ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new DouzeException($"cannot find {package} {version} on nuget.org: {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            var catalog = document.RootElement.GetProperty("catalogEntry").GetString() ?? throw new DouzeException($"{package} {version} has no catalog entry");

            using var entry = JsonDocument.Parse(await _http.GetStreamAsync(catalog, ct));
            var algorithm = entry.RootElement.GetProperty("packageHashAlgorithm").GetString();
            var hash = entry.RootElement.GetProperty("packageHash").GetString();

            return algorithm == "SHA512" && hash != null ? "sha512-" + hash : throw new DouzeException($"{package} {version} has no SHA-512 hash on nuget.org");
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new DouzeException($"cannot read {package} {version} from nuget.org: {e.Message}");
        }
    }

    /// <summary>
    /// The url of a package's tarball in the registry: <c>&lt;registry&gt;/@scope/name/-/name-version.tgz</c>.
    /// </summary>
    private string TarballUrl(string package, string version)
    {
        var name = package[(package.LastIndexOf('/') + 1)..];
        return new Uri(_registry, $"{package}/-/{name}-{version}.tgz").AbsoluteUri;
    }

    /// <summary>
    /// The tarball url and integrity of a version from the registry's document of that version.
    /// </summary>
    private async Task<(string Url, string Integrity)> ResolveAsync(string package, string version, CancellationToken ct)
    {
        var url = new Uri(_registry, $"{package.Replace("/", "%2F", StringComparison.Ordinal)}/{Uri.EscapeDataString(version)}");

        try
        {
            using var response = await _http.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new DouzeException($"cannot find {package}@{version} in the registry {_registry}: {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            var dist = document.RootElement.GetProperty("dist");
            var tarball = dist.GetProperty("tarball").GetString() ?? throw new DouzeException($"{package}@{version} has no tarball");
            var integrity = dist.TryGetProperty("integrity", out var sri) ? sri.GetString()
                : dist.TryGetProperty("shasum", out var shasum) ? "sha1-" + Convert.ToBase64String(Convert.FromHexString(shasum.GetString() ?? "")) : null;

            return (tarball, integrity ?? throw new DouzeException($"{package}@{version} has no integrity in the registry"));
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new DouzeException($"cannot read {package}@{version} from the registry {_registry}: {e.Message}");
        }
    }

    private async Task DownloadAsync(string url, string path, string integrity, CancellationToken ct)
    {
        var (algorithm, expected) = Integrity.Parse(integrity);
        using var hash = IncrementalHash.CreateHash(algorithm);

        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new DouzeException($"cannot download {url}: {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            int read;

            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        catch (HttpRequestException e)
        {
            throw new DouzeException($"cannot download {url}: {e.Message}");
        }

        var actual = hash.GetHashAndReset();

        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new DouzeException($"the download {url} does not match its integrity: expected {integrity}, got {Integrity.Format(algorithm, actual)}");
        }
    }

    /// <summary>
    /// Extracts a gzipped tarball into <paramref name="folder"/>: regular files and folders only, none outside it.
    /// </summary>
    private static void Extract(string archive, string folder)
    {
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);

        try
        {
            using var file = File.OpenRead(archive);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);

            while (reader.GetNextEntry() is { } entry)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory))
                {
                    continue;
                }

                var path = Path.GetFullPath(Path.Combine(root, entry.Name.TrimStart('/', '\\')));

                if (!path.StartsWith(root, StringComparison.Ordinal))
                {
                    throw new DouzeException($"the archive has an entry outside its folder: '{entry.Name}'");
                }

                if (entry.EntryType == TarEntryType.Directory)
                {
                    Directory.CreateDirectory(path);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path, overwrite: true);
            }
        }
        catch (Exception e) when (e is InvalidDataException or FormatException or EndOfStreamException)
        {
            throw new DouzeException($"cannot extract the package: {e.Message}");
        }
    }

    /// <summary>
    /// Extracts a zip (a <c>.nupkg</c>) into <paramref name="folder"/>: none of its entries outside it.
    /// </summary>
    private static void ExtractZip(string archive, string folder)
    {
        try
        {
            // Throws for an entry outside the folder.
            ZipFile.ExtractToDirectory(archive, folder);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            throw new DouzeException($"cannot extract the package: {e.Message}");
        }
    }

    private async Task<FileStream> LockAsync(string path, string name, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + LockTimeout;
        var waiting = false;

        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                if (!waiting)
                {
                    waiting = true;
                    _log(LogLevel.Info, $"waiting for another install of {name}");
                }

                await Task.Delay(100, ct);
            }
            catch (IOException)
            {
                throw new DouzeException($"another install of {name} did not finish within {LockTimeout.TotalMinutes:0} minutes ('{path}')");
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Subresource integrity strings (<c>sha512-&lt;base64&gt;</c>), as npm writes them.
/// </summary>
internal static class Integrity
{
    /// <summary>
    /// The strongest hash of an integrity string (several can be given, separated by spaces).
    /// </summary>
    /// <exception cref="DouzeException">No supported hash.</exception>
    public static (HashAlgorithmName Algorithm, byte[] Hash) Parse(string integrity)
    {
        (HashAlgorithmName, byte[])? best = null;
        var bestRank = 0;

        foreach (var part in integrity.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var dash = part.IndexOf('-', StringComparison.Ordinal);

            if (dash < 0)
            {
                continue;
            }

            var (algorithm, rank) = part[..dash] switch
            {
                "sha512" => (HashAlgorithmName.SHA512, 4),
                "sha384" => (HashAlgorithmName.SHA384, 3),
                "sha256" => (HashAlgorithmName.SHA256, 2),
                "sha1" => (HashAlgorithmName.SHA1, 1),
                _ => (default, 0),
            };

            if (rank <= bestRank)
            {
                continue;
            }

            try
            {
                best = (algorithm, Convert.FromBase64String(part[(dash + 1)..]));
                bestRank = rank;
            }
            catch (FormatException)
            {
            }
        }

        return best ?? throw new DouzeException($"'{integrity}' is not a supported integrity");
    }

    public static string Format(HashAlgorithmName algorithm, byte[] hash) => $"{algorithm.Name!.ToLowerInvariant()}-{Convert.ToBase64String(hash)}";
}
