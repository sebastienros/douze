using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Douze.Workflow;

namespace Douze.Markdown;

/// <summary>
/// The <c>markdown</c> section of <c>douze.yml</c>: the one Markdown pipeline of a build, used by the <c>markdown</c>
/// step and by everything else that renders Markdown.
/// </summary>
internal sealed record MarkdownOptions
{
    /// <summary>
    /// The extensions of a <c>markdown</c> section without <c>extensions</c>.
    /// </summary>
    public static readonly ImmutableArray<string> DefaultExtensions = ["pipetables", "footnotes", "tasklists", "definitionlists", "attributes", "autolinks", "alerts", "customcontainers"];

    /// <summary>
    /// The options of a site without a <c>markdown</c> section: CommonMark without Markdig extensions (the rendering of
    /// a site that has no <c>douze.yml</c>), with Douze's own markup (code fences, raw regions, <c>.md</c> links).
    /// </summary>
    public static readonly MarkdownOptions None = new() { Extensions = [] };

    private HashSlot _hash;

    // 'with' expressions must not copy the cached hash.
    private MarkdownOptions(MarkdownOptions original)
    {
        Extensions = original.Extensions;
        Aside = original.Aside;
        RewriteLinks = original.RewriteLinks;
        CodeClassPrefix = original.CodeClassPrefix;
        PassthroughLanguages = original.PassthroughLanguages;
        Images = original.Images;
    }

    public MarkdownOptions()
    {
    }

    /// <summary>
    /// Markdig extension names (<c>pipetables</c>, <c>footnotes</c>, ...), applied in this order but <c>attributes</c> last; <c>advanced</c> is
    /// Markdig's set without <c>autoidentifiers</c>.
    /// </summary>
    public ImmutableArray<string> Extensions { get; init; } = DefaultExtensions;

    public AsideOptions Aside { get; init; } = AsideOptions.Default;

    /// <summary>
    /// Whether relative links to <c>.md</c> files are rewritten to the urls of their pages.
    /// </summary>
    public bool RewriteLinks { get; init; } = true;

    /// <summary>
    /// The prefix of the language class of a fence's <c>code</c> element.
    /// </summary>
    public string CodeClassPrefix { get; init; } = "language-";

    /// <summary>
    /// Fence languages rendered as <c>&lt;pre class="mermaid"&gt;</c> without a <c>code</c> element, for a client script.
    /// </summary>
    public ImmutableArray<string> PassthroughLanguages { get; init; } = ["mermaid"];

    /// <summary>
    /// The defaults of the image features; read and validated, not used yet.
    /// </summary>
    public ImageOptions Images { get; init; } = ImageOptions.Default;

    /// <summary>
    /// A hash of every option: a result rendered with other options is not reused.
    /// </summary>
    /// <summary>
    /// Changes when the rendering of the same options changes (a Douze extension, a Markdig update): every step that
    /// renders Markdown folds <see cref="Hash"/> into its cache version. Version 2: typographic apostrophes.
    /// </summary>
    private static readonly Hash128 RenderingVersion = Hash128.OfText("markdown-2");

    public Hash128 Hash => _hash.Get(this, static x => Hash128.Combine(Hash128.OfText(x.Canonical()), RenderingVersion));

    public bool Equals(MarkdownOptions? other) => other is not null && (ReferenceEquals(this, other) || Hash == other.Hash);

    public override int GetHashCode() => Hash.GetHashCode();

    private string Canonical()
    {
        var builder = new StringBuilder();
        builder.Append("extensions=").AppendJoin('+', Extensions).Append('\n');
        builder.Append("aside.tag=").Append(Aside.Tag).Append('\n');
        builder.Append("aside.class=").Append(Aside.Class).Append('\n');
        builder.Append("aside.kinds=").AppendJoin('+', Aside.Kinds).Append('\n');

        foreach (var (kind, title) in Aside.Titles.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            builder.Append("aside.titles.").Append(kind).Append('=').Append(title.ReplaceLineEndings(" ")).Append('\n');
        }

        foreach (var (kind, icon) in Aside.Icons.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            builder.Append("aside.icons.").Append(kind).Append('=').Append(icon.ReplaceLineEndings(" ")).Append('\n');
        }

        builder.Append("aside.titles-from=").Append(Aside.TitlesFrom).Append('\n');
        builder.Append("links=").Append(RewriteLinks).Append('\n');
        builder.Append("code.class-prefix=").Append(CodeClassPrefix).Append('\n');
        builder.Append("code.passthrough=").AppendJoin('+', PassthroughLanguages).Append('\n');
        builder.Append("images.widths=").AppendJoin('+', Images.Widths.Select(x => x.ToString(CultureInfo.InvariantCulture))).Append('\n');
        builder.Append("images.formats=").AppendJoin('+', Images.Formats).Append('\n');
        builder.Append("images.quality=").Append(Images.Quality?.ToString(CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("images.output=").Append(Images.Output).Append('\n');
        builder.Append("images.loading=").Append(Images.Loading).Append('\n');
        return builder.ToString();
    }
}

/// <summary>
/// How <c>:::note[Title]</c> containers and <c>&gt; [!NOTE]</c> alerts render, as BEM classes derived from
/// <see cref="Class"/> so that a site can match its existing styles:
/// <c>&lt;aside aria-label="Note" class="aside aside--note"&gt;&lt;p class="aside__title" aria-hidden="true"&gt;{icon}Note&lt;/p&gt;&lt;div class="aside__content"&gt;...&lt;/div&gt;&lt;/aside&gt;</c>.
/// </summary>
/// <param name="Tag">The element.</param>
/// <param name="Class">The block class; <c>&lt;class&gt;--&lt;kind&gt;</c>, <c>&lt;class&gt;__title</c> and <c>&lt;class&gt;__content</c> derive from it.</param>
/// <param name="Kinds">The known kinds; another kind renders as the first one (<c>note</c>) with a warning.</param>
/// <param name="Titles">The default title of each kind; a kind without one is its name, capitalized.</param>
/// <param name="Icons">Markup (an inline SVG) written before the title of each kind; none by default.</param>
/// <param name="TitlesFrom">A data key whose <c>&lt;locale&gt;.aside.&lt;kind&gt;</c> values localize the titles; not used until locales exist.</param>
internal sealed record AsideOptions(string Tag, string Class, ImmutableArray<string> Kinds, ImmutableDictionary<string, string> Titles, ImmutableDictionary<string, string> Icons, string? TitlesFrom)
{
    public static readonly AsideOptions Default = new(
        "aside",
        "aside",
        ["note", "tip", "caution", "danger"],
        ImmutableDictionary.CreateRange(StringComparer.Ordinal, new Dictionary<string, string>
        {
            ["note"] = "Note",
            ["tip"] = "Tip",
            ["caution"] = "Caution",
            ["danger"] = "Danger",
        }),
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal),
        null);

    /// <summary>
    /// The default title of <paramref name="kind"/>.
    /// </summary>
    public string Title(string kind)
    {
        if (Titles.TryGetValue(kind, out var title))
        {
            return title;
        }

        return kind.Length == 0 ? kind : char.ToUpperInvariant(kind[0]) + kind[1..];
    }
}

/// <summary>
/// The <c>images</c> defaults of the <c>markdown</c> section, for the image features.
/// </summary>
internal sealed record ImageOptions(ImmutableArray<int> Widths, ImmutableArray<string> Formats, int? Quality, string? Output, string? Loading)
{
    public static readonly ImageOptions Default = new([], [], null, null, null);
}
