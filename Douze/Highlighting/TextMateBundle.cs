using System.IO.Compression;
using System.Text.Json;
using Tint.Grammars;
using Tint.Loading;

namespace Douze.Highlighting;

/// <summary>
/// A TextMate grammar the highlighter knows: its name, the other names fences may use for it, its root scope, the
/// languages it embeds and the scopes it is injected into when it is an injection grammar. <paramref name="Source"/>
/// gives the grammar itself, read on first use. An injection grammar of Tint (<paramref name="AlwaysInject"/>) applies
/// to every language, as in VS Code; one of tm-grammars only to the languages that embed it, as in Shiki.
/// </summary>
internal sealed record CodeGrammar(string Name, string ScopeName, IReadOnlyList<string> Aliases, IReadOnlyList<string> Embedded, IReadOnlyList<string> InjectTo, Func<IGrammarSource> Source, bool AlwaysInject = false)
{
    /// <summary>
    /// The grammars of Tint (the VS Code grammars of Tint.Grammars), which win over the grammars of
    /// <see cref="TextMateBundle"/>.
    /// </summary>
    public static IReadOnlyList<CodeGrammar> Tint { get; } = [.. global::Tint.Grammars.TintGrammars.All.Select(FromTint)];

    /// <summary>
    /// A grammar of Tint, named by its language, or by its scope when it has none (injection grammars, grammars only
    /// included by others).
    /// </summary>
    public static CodeGrammar FromTint(IGrammarSource source) =>
        new(source.Info.LanguageId ?? source.ScopeName, source.ScopeName, [], [], source.Info.InjectTo, () => source, AlwaysInject: true);

    /// <summary>
    /// A grammar read from TextMate JSON, with the scopes it is injected into.
    /// </summary>
    public static IGrammarSource Load(byte[] json, IReadOnlyList<string> injectTo) =>
        GrammarLoader.Load(json, DocumentFormat.Json, injectTo.Count == 0 ? null : new GrammarInfo { InjectTo = injectTo });
}

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

    private TextMateBundle(Archive archive, IReadOnlyList<CodeGrammar> grammars, IReadOnlyList<(string Name, string Type)> themes, string version)
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
    public IReadOnlyList<CodeGrammar> Grammars { get; }

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
        var grammars = new List<CodeGrammar>();
        var themes = new List<(string, string)>();

        foreach (var list in new[] { "grammars", "injections" })
        {
            foreach (var item in root.GetProperty("tm-grammars").GetProperty("lists").GetProperty(list).EnumerateArray())
            {
                var name = item.GetProperty("name").GetString()!;
                var path = $"grammars/{name}.json";
                var injectTo = Strings(item, "injectTo");
                var source = new Lazy<IGrammarSource>(() => CodeGrammar.Load(archive.Read(path)!, injectTo));
                grammars.Add(new CodeGrammar(
                    name,
                    item.GetProperty("scopeName").GetString()!,
                    Strings(item, "aliases"),
                    Strings(item, "embedded"),
                    injectTo,
                    () => source.Value));
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
