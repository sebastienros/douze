using System.IO.Compression;
using System.Text.Json;

namespace Douze.Highlighting;

/// <summary>
/// A TextMate grammar the highlighter knows: its name, the other names fences may use for it, its root scope, the
/// languages it embeds (loaded with it) and the scopes it is injected into when it is an injection grammar.
/// </summary>
internal sealed record GrammarInfo(string Name, string ScopeName, string? DisplayName, IReadOnlyList<string> Aliases, IReadOnlyList<string> Embedded, IReadOnlyList<string> InjectTo, Func<byte[]> Load);

/// <summary>
/// The TextMate grammars and themes bundled with Douze (<c>textmate.zip</c>, written by
/// <c>scripts/update-textmate.cs</c>: every grammar of the tm-grammars package and every theme of tm-themes, the
/// versions a highlighter of reference bundles), decompressed on first use.
/// </summary>
internal sealed class TextMateBundle
{
    private const string ResourceName = "textmate.zip";

    private static readonly Lazy<TextMateBundle> Bundled = new(Load);

    private readonly Archive _archive;

    private TextMateBundle(Archive archive, IReadOnlyList<GrammarInfo> grammars, IReadOnlyList<(string Name, string Type)> themes, string version)
    {
        _archive = archive;
        Grammars = grammars;
        Themes = themes;
        Version = version;
    }

    /// <summary>
    /// The bundle of the binary.
    /// </summary>
    public static TextMateBundle Default => Bundled.Value;

    /// <summary>
    /// The grammars, then the injection grammars.
    /// </summary>
    public IReadOnlyList<GrammarInfo> Grammars { get; }

    /// <summary>
    /// The names and types (<c>dark</c>, <c>light</c>) of the themes.
    /// </summary>
    public IReadOnlyList<(string Name, string Type)> Themes { get; }

    /// <summary>
    /// The versions of the packages the bundle was made from.
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// The JSON of a bundled theme, or null.
    /// </summary>
    public byte[]? GetTheme(string name) => _archive.Read($"themes/{name}.json");

    private static TextMateBundle Load()
    {
        var stream = typeof(TextMateBundle).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"the resource {ResourceName} is missing");
        var archive = new Archive(new ZipArchive(stream, ZipArchiveMode.Read));
        using var manifest = JsonDocument.Parse(archive.Read("manifest.json")!);
        var root = manifest.RootElement;
        var grammars = new List<GrammarInfo>();
        var themes = new List<(string, string)>();

        foreach (var list in new[] { "grammars", "injections" })
        {
            foreach (var item in root.GetProperty("tm-grammars").GetProperty("lists").GetProperty(list).EnumerateArray())
            {
                var name = item.GetProperty("name").GetString()!;
                var path = $"grammars/{name}.json";
                grammars.Add(new GrammarInfo(
                    name,
                    item.GetProperty("scopeName").GetString()!,
                    item.TryGetProperty("displayName", out var display) ? display.GetString() : null,
                    Strings(item, "aliases"),
                    Strings(item, "embedded"),
                    Strings(item, "injectTo"),
                    () => archive.Read(path)!));
            }
        }

        foreach (var item in root.GetProperty("tm-themes").GetProperty("lists").GetProperty("themes").EnumerateArray())
        {
            themes.Add((item.GetProperty("name").GetString()!, item.TryGetProperty("type", out var type) ? type.GetString() ?? "dark" : "dark"));
        }

        var version = $"tm-grammars@{root.GetProperty("tm-grammars").GetProperty("version").GetString()}+tm-themes@{root.GetProperty("tm-themes").GetProperty("version").GetString()}";
        return new TextMateBundle(archive, grammars, themes, version);
    }

    private static string[] Strings(JsonElement item, string name) =>
        item.TryGetProperty(name, out var array) ? [.. array.EnumerateArray().Select(x => x.GetString()!)] : [];

    /// <summary>
    /// The archive, whose files are decompressed once, on first read.
    /// </summary>
    private sealed class Archive(ZipArchive zip)
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, byte[]?> _files = new(StringComparer.Ordinal);

        public byte[]? Read(string path)
        {
            lock (_lock)
            {
                if (!_files.TryGetValue(path, out var bytes))
                {
                    if (zip.GetEntry(path) is { } entry)
                    {
                        using var entryStream = entry.Open();
                        using var memory = new MemoryStream((int)entry.Length);
                        entryStream.CopyTo(memory);
                        bytes = memory.ToArray();
                    }

                    _files[path] = bytes;
                }

                return bytes;
            }
        }
    }
}
