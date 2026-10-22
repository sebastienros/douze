using System.Collections.Immutable;
using Douze.FileSystems;
using Douze.Nodes;

namespace Douze.Workflow;

/// <summary>
/// One locale of a site: its key (the folder of its pages, or the default locale's key), what templates show of it,
/// and the prefix of its urls (empty for the default locale, <c>/ja</c> for the folder <c>ja</c>).
/// </summary>
internal sealed record Locale(string Key, string Label, string Lang, string Dir, string Prefix)
{
    public bool IsDefault => Prefix.Length == 0;
}

/// <summary>
/// The <c>site.locales</c> settings of the workflow file: the default locale, whose pages have no url prefix, the folder
/// whose children are the locale folders (<see cref="Root"/>), the languages by folder, whether untranslated pages are
/// copied under every locale (<see cref="Fallback"/>), and the data key of the UI strings. <see cref="None"/> when the
/// section is absent: a single locale, no prefixes.
/// </summary>
internal sealed class LocaleSet
{
    public const string DefaultStrings = "i18n";

    public static readonly LocaleSet None = new();

    private readonly Dictionary<string, Locale> _byKey;
    private NodeObject? _node;
    private HashSlot _hash;

    private LocaleSet()
    {
        Default = new Locale("", "", "", "ltr", "");
        Languages = [];
        All = [];
        Strings = DefaultStrings;
        _byKey = new(StringComparer.Ordinal);
    }

    /// <param name="defaultLocale">The settings of the default locale.</param>
    /// <param name="languages">The locale folders, in declared order, without the default locale.</param>
    /// <param name="rootFolder">The folder whose children are the locale folders, relative to the content folder; null for the content folder itself.</param>
    /// <param name="fallback">Whether the default locale's pages are copied under the locales that do not translate them.</param>
    /// <param name="strings">The data key of the UI strings.</param>
    public LocaleSet(Locale defaultLocale, ImmutableArray<Locale> languages, string? rootFolder, bool fallback, string strings)
    {
        Default = defaultLocale;
        Languages = languages;
        All = [defaultLocale, .. languages];
        RootFolder = rootFolder;
        Root = rootFolder == null ? FPath.Root : Steps.StepPaths.Directory(rootFolder);
        Fallback = fallback;
        Strings = strings;
        IsEnabled = true;
        _byKey = All.ToDictionary(x => x.Key, StringComparer.Ordinal);
    }

    private LocaleSet(LocaleSet source, FPath root)
    {
        Default = source.Default;
        Languages = source.Languages;
        All = source.All;
        RootFolder = source.RootFolder;
        Root = root;
        Fallback = source.Fallback;
        Strings = source.Strings;
        IsEnabled = source.IsEnabled;
        _byKey = source._byKey;
    }

    public bool IsEnabled { get; }

    public Locale Default { get; }

    /// <summary>
    /// The locales with a folder, in declared order.
    /// </summary>
    public ImmutableArray<Locale> Languages { get; }

    /// <summary>
    /// The default locale, then <see cref="Languages"/>.
    /// </summary>
    public ImmutableArray<Locale> All { get; }

    /// <summary>
    /// <c>root</c> as written in the workflow file, relative to the content folder.
    /// </summary>
    public string? RootFolder { get; }

    /// <summary>
    /// The folder whose children are the locale folders, an absolute directory of the source tree once
    /// <see cref="WithContentFolder"/> resolved it.
    /// </summary>
    public FPath Root { get; }

    public bool Fallback { get; }

    public string Strings { get; }

    /// <summary>
    /// Resolves <see cref="Root"/> under <paramref name="contentFolder"/>.
    /// </summary>
    public LocaleSet WithContentFolder(FPath contentFolder)
    {
        if (!IsEnabled)
        {
            return this;
        }

        var root = RootFolder == null ? contentFolder : FPath.Combine(contentFolder, Steps.StepPaths.Directory(RootFolder).ToRelative());
        return new LocaleSet(this, root);
    }

    public bool TryGet(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Locale? locale) => _byKey.TryGetValue(key, out locale);

    /// <summary>
    /// Whether <paramref name="directory"/> (a source directory) is <see cref="Root"/> or under it.
    /// </summary>
    public bool Contains(FPath directory)
    {
        return IsEnabled && directory.FullName.StartsWith(Root.FullName, StringComparison.Ordinal);
    }

    /// <summary>
    /// The locale of a page of <paramref name="directory"/>, a directory under <see cref="Root"/>: the locale whose folder
    /// is the first one below the root, the default locale otherwise.
    /// </summary>
    public Locale LocaleOfDirectory(FPath directory)
    {
        var relative = directory.FullName.AsSpan(Root.FullName.Length);
        var slash = relative.IndexOf('/');

        if (slash > 0 && _byKey.TryGetValue(relative[..slash].ToString(), out var locale) && !locale.IsDefault)
        {
            return locale;
        }

        return Default;
    }

    /// <summary>
    /// The locale of a url: the locale whose prefix starts it (<c>/ja/x/</c>), the default locale otherwise.
    /// </summary>
    public Locale LocaleOfUrl(string url)
    {
        foreach (var locale in Languages)
        {
            if (HasPrefix(url, locale.Prefix))
            {
                return locale;
            }
        }

        return Default;
    }

    /// <summary>
    /// <paramref name="url"/> without the prefix of its locale: <c>/ja/x/</c> is <c>/x/</c>, <c>/ja/</c> is <c>/</c>.
    /// </summary>
    public string StripPrefix(string url)
    {
        var locale = LocaleOfUrl(url);
        return locale.IsDefault ? url : url[locale.Prefix.Length..];
    }

    /// <summary>
    /// Whether <paramref name="url"/> starts with the locale prefix <paramref name="prefix"/> followed by a slash.
    /// </summary>
    public static bool HasPrefix(string url, string prefix)
    {
        return prefix.Length > 0 && url.Length > prefix.Length && url.StartsWith(prefix, StringComparison.Ordinal) && url[prefix.Length] == '/';
    }

    /// <summary>
    /// A url without its trailing <c>index.html</c>: <c>/docs/a/index.html</c> is <c>/docs/a/</c>.
    /// </summary>
    public static string NormalizeUrl(string url)
    {
        const string Index = "index.html";
        return url.EndsWith("/" + Index, StringComparison.Ordinal) ? url[..^Index.Length] : url;
    }

    /// <summary>
    /// What templates see under <c>site.locales</c>: <c>default</c>, <c>fallback</c>, <c>strings</c> and
    /// <c>languages</c>, the list of every locale (<c>locale</c>, <c>label</c>, <c>lang</c>, <c>dir</c>, <c>prefix</c>),
    /// the default locale first.
    /// </summary>
    public NodeObject ToNode()
    {
        return _node ??= Build();

        NodeObject Build()
        {
            var languages = new NodeArray();

            foreach (var locale in All)
            {
                languages.Add(new NodeObject
                {
                    ["locale"] = NodeValue.Create(locale.Key),
                    ["label"] = NodeValue.Create(locale.Label),
                    ["lang"] = NodeValue.Create(locale.Lang),
                    ["dir"] = NodeValue.Create(locale.Dir),
                    ["prefix"] = NodeValue.Create(locale.Prefix),
                });
            }

            return new NodeObject
            {
                ["default"] = NodeValue.Create(Default.Key),
                ["fallback"] = NodeValue.Create(Fallback),
                ["strings"] = NodeValue.Create(Strings),
                ["languages"] = languages,
            };
        }
    }

    /// <summary>
    /// The hash of every setting, for the result keys of the steps that read them.
    /// </summary>
    public Hash128 Hash => _hash.Get(this, static x => x.IsEnabled ? Hash128.Combine(NodeHash.Of(x.ToNode()), Hash128.OfText(x.Root.FullName)) : Hash128.Empty);
}
