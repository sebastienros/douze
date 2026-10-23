// Writes Douze/Highlighting/textmate.zip, the TextMate grammars and themes the `highlight` step bundles: every grammar
// of the tm-grammars npm package and every theme of tm-themes, at the versions below, with a manifest of their names,
// aliases and embedded languages (the packages' index.js) and their licenses.
//
//     dotnet run scripts/update-textmate.cs
//
// Pin the versions a highlighter of reference uses (Shiki bundles them), with the integrity the npm registry gives for
// each tarball (`npm view tm-grammars@<version> dist.integrity`).

// File-based apps default to native AOT, whose analyzers reject the reflection based JSON serialization below.
#:property PublishAot=false

using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

(string Name, string Version, string Integrity)[] packages =
[
    ("tm-grammars", "1.32.22", "sha512-VKWL+UiqlGqbGHonRbrBc0kkqDIHUUiVO/A2rBTBz5LBXKA8Fe2f34O1YiBI7ED37tTUlT3MGmLf9+0F/H4/Ww=="),
    ("tm-themes", "1.12.12", "sha512-okyxQlASpDOtUA40+ZT0IGQyfObGOknntHY3kujfUlD8lj3ERMObDJNRgvlc/baVMsnJVQmtAB2nE7LFGTo0qQ=="),
];

var repository = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(AppContext.GetData("EntryPointFilePath") as string ?? "scripts/update-textmate.cs")))!;
var output = Path.Combine(repository, "Douze", "Highlighting", "textmate.zip");

using var http = new HttpClient();
var entries = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
var manifest = new JsonObject();

foreach (var (name, version, integrity) in packages)
{
    var bytes = await http.GetByteArrayAsync($"https://registry.npmjs.org/{name}/-/{name}-{version}.tgz");
    var actual = "sha512-" + Convert.ToBase64String(SHA512.HashData(bytes));

    if (actual != integrity)
    {
        throw new InvalidOperationException($"{name}@{version}: integrity {actual}, expected {integrity}");
    }

    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);

    using (var tar = new TarReader(new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress)))
    {
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.DataStream is { } data && entry.Name.StartsWith("package/", StringComparison.Ordinal))
            {
                using var memory = new MemoryStream();
                data.CopyTo(memory);
                files[entry.Name["package/".Length..]] = memory.ToArray();
            }
        }
    }

    var folder = name == "tm-grammars" ? "grammars" : "themes";

    foreach (var (path, content) in files)
    {
        if (path.StartsWith(folder + "/", StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.Ordinal))
        {
            // Compact: the JSON is parsed once per build.
            entries[path] = Encoding.UTF8.GetBytes(JsonNode.Parse(content)!.ToJsonString());
        }
        else if (path is "LICENSE" or "NOTICE")
        {
            entries[$"{name}/{path}"] = content;
        }
    }

    // index.js: `export const grammars = [ ... ]` (and `injections`), JavaScript object literals.
    var index = Encoding.UTF8.GetString(files["index.js"]);
    var lists = new JsonObject();

    foreach (var list in name == "tm-grammars" ? new[] { "grammars", "injections" } : ["themes"])
    {
        var start = index.IndexOf($"export const {list} = ", StringComparison.Ordinal);
        var literal = JavaScriptLiteral(index, index.IndexOf('[', start));
        var items = JsonNode.Parse(literal)!.AsArray();
        var kept = new JsonArray();

        foreach (var item in items)
        {
            var entry = new JsonObject();

            foreach (var key in new[] { "name", "scopeName", "displayName", "type", "aliases", "embedded", "injectTo" })
            {
                if (item![key] is { } value)
                {
                    entry[key] = value.DeepClone();
                }
            }

            kept.Add(entry);
        }

        lists[list] = kept;
    }

    manifest[name] = new JsonObject { ["version"] = version, ["lists"] = lists };
}

entries["manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

// A deterministic archive: sorted entries, a fixed time.
var time = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

using (var stream = File.Create(output))
using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
{
    foreach (var (path, content) in entries)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.SmallestSize);
        entry.LastWriteTime = time;
        using var entryStream = entry.Open();
        entryStream.Write(content);
    }
}

Console.WriteLine($"{output}: {entries.Count} files, {new FileInfo(output).Length / 1024} KB");

// The JSON of the JavaScript literal (arrays, objects, single quoted strings, unquoted keys, trailing commas) that
// starts at `start`.
static string JavaScriptLiteral(string text, int start)
{
    var builder = new StringBuilder();
    var depth = 0;

    for (var i = start; i < text.Length; i++)
    {
        var c = text[i];

        if (c == '\'' || c == '"')
        {
            var value = new StringBuilder();

            for (i++; text[i] != c; i++)
            {
                if (text[i] == '\\')
                {
                    i++;
                    value.Append(text[i] switch { 'n' => '\n', 't' => '\t', var other => other });
                }
                else
                {
                    value.Append(text[i]);
                }
            }

            builder.Append(JsonSerializer.Serialize(value.ToString()));
        }
        else if (char.IsLetter(c) || c == '_' || c == '$')
        {
            var end = i;

            while (char.IsLetterOrDigit(text[end]) || text[end] is '_' or '$')
            {
                end++;
            }

            var word = text[i..end];
            builder.Append(word is "true" or "false" or "null" ? word : JsonSerializer.Serialize(word));
            i = end - 1;
        }
        else if (c is ']' or '}')
        {
            // No trailing commas in JSON.
            var last = builder.Length - 1;

            while (last >= 0 && char.IsWhiteSpace(builder[last]))
            {
                last--;
            }

            if (last >= 0 && builder[last] == ',')
            {
                builder.Remove(last, 1);
            }

            builder.Append(c);

            if (--depth == 0)
            {
                break;
            }
        }
        else
        {
            if (c is '[' or '{')
            {
                depth++;
            }

            builder.Append(c);
        }
    }

    return builder.ToString();
}
