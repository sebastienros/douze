// Serves a folder of .nupkg files as a NuGet v3 feed (service index and flat container), the parts the install
// scripts read, so that CI tests them against the packages it just built.
//
//     dotnet run scripts/test-feed.cs -- <folder of .nupkg files> [port]
//
// The service index is http://localhost:<port>/index.json (port 5555 by default); GET /shutdown stops the server.

#:package NuGet.Versioning
// File-based apps default to native AOT, whose analyzers reject the reflection based JSON serialization below.
#:property PublishAot=false

using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using NuGet.Versioning;

var folder = Path.GetFullPath(args[0]);
var port = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 5555;
var prefix = $"http://localhost:{port}/";

// The packages by lowercase id, then version.
var packages = new Dictionary<string, SortedDictionary<NuGetVersion, string>>();
foreach (var path in Directory.GetFiles(folder, "*.nupkg").Order(StringComparer.Ordinal))
{
    using var zip = ZipFile.OpenRead(path);
    var nuspec = zip.Entries.First(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal) && !entry.FullName.Contains('/'));
    using var reader = new StreamReader(nuspec.Open());
    var text = reader.ReadToEnd();
    var id = Regex.Match(text, "<id>([^<]+)</id>").Groups[1].Value.ToLowerInvariant();
    var version = NuGetVersion.Parse(Regex.Match(text, "<version>([^<]+)</version>").Groups[1].Value);
    if (!packages.TryGetValue(id, out var versions))
    {
        packages[id] = versions = [];
    }

    versions[version] = path;
}

var serviceIndex = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
{
    ["version"] = "3.0.0",
    ["resources"] = new[] { new Dictionary<string, string> { ["@id"] = $"{prefix}flat/", ["@type"] = "PackageBaseAddress/3.0.0" } },
});

using var listener = new HttpListener();
listener.Prefixes.Add(prefix);
listener.Start();
Console.WriteLine($"{prefix}index.json: {string.Join(", ", packages.SelectMany(p => p.Value.Keys.Select(v => $"{p.Key} {v.ToNormalizedString()}")))}");

while (true)
{
    var context = await listener.GetContextAsync();
    var response = context.Response;
    var segments = context.Request.Url!.AbsolutePath.Trim('/').Split('/');
    try
    {
        if (segments is ["shutdown"])
        {
            response.Close();
            break;
        }

        if (segments is ["index.json"])
        {
            await WriteAsync(response, "application/json", serviceIndex);
        }
        else if (segments is ["flat", var id, "index.json"] && packages.TryGetValue(id, out var versions))
        {
            var list = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string[]>
            {
                ["versions"] = [.. versions.Keys.Select(v => v.ToNormalizedString().ToLowerInvariant())],
            });
            await WriteAsync(response, "application/json", list);
        }
        else if (segments is ["flat", var packageId, var version, var file]
            && packages.TryGetValue(packageId, out var packageVersions)
            && NuGetVersion.TryParse(version, out var parsed)
            && packageVersions.TryGetValue(parsed, out var path)
            && file == $"{packageId}.{version}.nupkg")
        {
            await WriteAsync(response, "application/octet-stream", await File.ReadAllBytesAsync(path));
        }
        else
        {
            response.StatusCode = 404;
            response.Close();
        }
    }
    catch (HttpListenerException)
    {
        // The client went away.
    }
}

static async Task WriteAsync(HttpListenerResponse response, string contentType, byte[] content)
{
    response.ContentType = contentType;
    response.ContentLength64 = content.Length;
    await response.OutputStream.WriteAsync(content);
    response.Close();
}
