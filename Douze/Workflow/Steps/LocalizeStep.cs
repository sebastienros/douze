using Douze.Nodes;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: localize</c>. Sets the locale of every page under the locale root (<c>site.locales.root</c>): <c>locale</c>
/// (the key of the locale folder the page is in, or the default locale), <c>lang</c>, <c>dir</c>, <c>locale_url</c>
/// (its normalized url without the locale prefix: what the page is called in every locale) and <c>alternates</c>, the
/// <c>{ locale, lang, url }</c> of every locale that serves a page at that <c>locale_url</c>, found in
/// <c>collections.urls</c> (<c>with.collections</c> names another collect step's scope key). With
/// <c>site.locales.fallback</c>, a page of the default locale is also copied under every locale that has no page at its
/// url: the copy shares the content, and has the locale's <c>locale</c>, <c>lang</c>, <c>dir</c>, the prefixed
/// <c>url</c>, <c>fallback: true</c> and <c>original_url</c>. A locale then serves every url the default locale has,
/// translated or not, so the alternates of those pages, and of their copies, list every locale. Pages outside the
/// locale root pass through.
/// </summary>
/// <remarks>
/// Runs after <c>permalink</c> and <c>collect</c>. A page depends on the presence of the urls it looked for in
/// <c>collections.urls</c> only (<see cref="ScopeSet.TryResolveTracked"/>): adding a translation runs the localize units
/// of the pages of that url again, and no other.
/// </remarks>
internal sealed class LocalizeStep : Step, IMapStep
{
    private static readonly GlobSet Pages = GlobSet.Parse(["**/*.html", "**/*.md"]);

    private readonly string _collections;
    private LocaleSet _locales = LocaleSet.None;

    public LocalizeStep(StepDefinition definition) : base(definition)
    {
        var options = new WithOptions(definition, "collections");
        _collections = options.GetString("collections", CollectStep.Collections)!;
    }

    public override StepShape NativeShape => StepShape.Map;

    public override GlobSet DefaultMatch => Pages;

    // The locale settings are read from the build, not through the scopes.
    public override Hash128 CacheVersion => _locales.Hash;

    public override ValueTask InitializeAsync(BuildContext context, CancellationToken ct)
    {
        _locales = context.Locales;
        return default;
    }

    public ValueTask<FileList> ProcessAsync(VirtualFile file, StepContext context, CancellationToken ct)
    {
        var locales = _locales;
        var metadata = file.Metadata;

        if (!locales.Contains(metadata.Directory))
        {
            return new(FileList.Of(file));
        }

        var locale = locales.LocaleOfDirectory(metadata.Directory);
        var url = metadata.Own["url"] is NodeValue<string> { Value: { Length: > 0 } u } ? u : null;
        var normalized = url == null ? null : LocaleSet.NormalizeUrl(url);
        var localeUrl = normalized == null ? null : !locale.IsDefault && LocaleSet.HasPrefix(normalized, locale.Prefix) ? normalized[locale.Prefix.Length..] : normalized;

        // Whether each locale has a page at the url: the page's own locale does.
        var exists = new bool[locales.All.Length];

        if (localeUrl != null)
        {
            var urls = TrackedUrls(metadata, context);

            for (var i = 0; i < locales.All.Length; i++)
            {
                var other = locales.All[i];
                exists[i] = other == locale || urls.Contains(other.Prefix + localeUrl);
            }
        }

        // With fallback, a url of the default locale (All[0]) is served in every locale, by a translation or a copy.
        var servedEverywhere = locales.Fallback && exists[0];
        var alternates = new NodeArray();

        for (var i = 0; i < locales.All.Length; i++)
        {
            if (exists[i] || servedEverywhere)
            {
                var other = locales.All[i];

                alternates.Add(new NodeObject
                {
                    ["locale"] = NodeValue.Create(other.Key),
                    ["lang"] = NodeValue.Create(other.Lang),
                    ["url"] = NodeValue.Create(other.Prefix + localeUrl),
                });
            }
        }

        var localized = metadata.With(own =>
        {
            SetLocale(own, locale);

            if (localeUrl != null)
            {
                own["locale_url"] = NodeValue.Create(localeUrl);
                own["alternates"] = alternates;
            }
        });

        var page = file.With(Name, metadata: localized);

        if (!locales.Fallback || !locale.IsDefault || url == null)
        {
            return new(FileList.Of(page));
        }

        var outputs = new List<VirtualFile> { page };

        for (var i = 1; i < locales.All.Length; i++)
        {
            if (exists[i])
            {
                continue;
            }

            var other = locales.All[i];

            var copy = localized.With(own =>
            {
                SetLocale(own, other);
                own["url"] = NodeValue.Create(other.Prefix + url);
                own["fallback"] = NodeValue.Create(true);
                own["original_url"] = NodeValue.Create(url);
            });

            outputs.Add(file.With(Name, path: new(other.Prefix + file.Path.FullName), metadata: copy));
        }

        return new(FileList.Of(outputs));
    }

    private static void SetLocale(NodeObject own, Locale locale)
    {
        own["locale"] = NodeValue.Create(locale.Key);
        own["lang"] = NodeValue.Create(locale.Lang);
        own["dir"] = NodeValue.Create(locale.Dir);
    }

    /// <summary>
    /// <c>collections.urls</c>, read so that the unit depends on the presence of the urls it looks for only, like a
    /// template that reads <c>collections.urls["/ja/x/"]</c>.
    /// </summary>
    private TrackedLookup TrackedUrls(Metadata metadata, StepContext context)
    {
        if (!context.Scopes.TryResolveTracked(metadata, _collections, null, context.Dependencies, out var node, out var recorder))
        {
            return default;
        }

        var urlsRecorder = recorder?.Member(CollectStep.Urls);

        if (node is not NodeObject collections || collections[CollectStep.Urls] is not NodeObject urls)
        {
            // What was found is not what a collect step contributes: depend on all of it.
            urlsRecorder?.ReadWhole();
            return default;
        }

        return new TrackedLookup(urls, urlsRecorder);
    }

    private readonly record struct TrackedLookup(NodeObject? Urls, AccessRecorder? Recorder)
    {
        public bool Contains(string url)
        {
            // Nothing is read of the item: the unit depends on whether it is there.
            Recorder?.Member(url);
            return Urls != null && Urls.ContainsKey(url);
        }
    }
}
