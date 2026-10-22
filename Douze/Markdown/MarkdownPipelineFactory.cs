using System.Collections.Frozen;
using System.Collections.Immutable;
using Douze.Workflow;
using Markdig;

namespace Douze.Markdown;

/// <summary>
/// Builds the Markdig pipeline of <see cref="MarkdownOptions"/>: the Markdig extensions by name, then Douze's raw
/// regions, asides (with <c>alerts</c> or <c>customcontainers</c>) and code fence markup. Heading ids come from
/// <c>{#id}</c> (<c>attributes</c>) only; the <c>headings</c> step assigns the others.
/// </summary>
internal static class MarkdownPipelineFactory
{
    /// <summary>
    /// The extensions of Markdig's <c>advanced</c> set, in its order, without <c>autoidentifiers</c>.
    /// </summary>
    public static readonly ImmutableArray<string> Advanced =
    [
        "alerts", "abbreviations", "citations", "customcontainers", "definitionlists", "emphasisextras", "figures",
        "footers", "footnotes", "gridtables", "mathematics", "medialinks", "pipetables", "listextras", "tasklists",
        "diagrams", "autolinks", "attributes",
    ];

    /// <summary>
    /// The names <see cref="Validate"/> accepts: Markdig's, <c>none</c>, and <c>advanced</c>.
    /// </summary>
    public static readonly FrozenSet<string> Names = new[]
    {
        "none", "common", "advanced", "alerts", "pipetables", "gfm-pipetables", "emphasisextras", "listextras",
        "hardlinebreak", "footnotes", "footers", "citations", "attributes", "gridtables", "abbreviations", "emojis",
        "definitionlists", "customcontainers", "figures", "mathematics", "bootstrap", "medialinks", "smartypants",
        "tasklists", "diagrams", "nofollowlinks", "noopenerlinks", "noreferrerlinks", "nohtml", "yaml",
        "nonascii-noescape", "autolinks", "globalization", "cjk-friendly-emphasis",
    }.ToFrozenSet(StringComparer.Ordinal);

    // Other spellings people use for an extension, for the suggestion of an unknown name.
    private static readonly FrozenDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["tables"] = "pipetables",
        ["tasks"] = "tasklists",
        ["containers"] = "customcontainers",
        ["admonitions"] = "customcontainers",
        ["callouts"] = "alerts",
        ["emoji"] = "emojis",
        ["math"] = "mathematics",
        ["genericattributes"] = "attributes",
        ["definitions"] = "definitionlists",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Returns why <paramref name="name"/> is not a valid extension name, or <c>null</c> when it is.
    /// </summary>
    public static string? Validate(string name)
    {
        if (Names.Contains(name))
        {
            return null;
        }

        if (name == "autoidentifiers")
        {
            return "use the headings step for heading ids";
        }

        var suggestion = WorkflowValidationException.Suggest(name, Names);

        if (suggestion.Length == 0 && WorkflowValidationException.Suggest(name, Aliases.Keys) is { Length: > 0 })
        {
            var alias = Aliases.Keys.Order(StringComparer.Ordinal).MinBy(x => Distance(name, x))!;
            suggestion = $"; did you mean '{Aliases[alias]}'?";
        }

        return $"unknown Markdown extension '{name}'{suggestion}";
    }

    public static MarkdownPipeline Create(MarkdownOptions options)
    {
        var builder = new MarkdownPipelineBuilder();
        var names = Expand(options.Extensions);

        foreach (var name in names)
        {
            if (Validate(name) is { } error)
            {
                throw new ArgumentException(error, nameof(options));
            }

            builder.Configure(name);
        }

        builder.Extensions.Add(new RawRegionExtension());

        if (names.Contains("alerts") || names.Contains("customcontainers"))
        {
            builder.Extensions.Add(new AsideExtension(options.Aside));
        }

        builder.Extensions.Add(new CodeFenceRenderer.Extension(options.CodeClassPrefix, options.PassthroughLanguages));

        return builder.Build();
    }

    /// <summary>
    /// The names with <c>advanced</c> expanded, <c>none</c> and duplicates removed, and <c>attributes</c> last: it applies
    /// to the parsers that are in the pipeline when it is added.
    /// </summary>
    private static List<string> Expand(ImmutableArray<string> names)
    {
        var result = new List<string>();

        foreach (var name in names)
        {
            foreach (var item in name == "advanced" ? Advanced : [name])
            {
                if (item != "none" && !result.Contains(item))
                {
                    result.Add(item);
                }
            }
        }

        if (result.Remove("attributes"))
        {
            result.Add("attributes");
        }

        return result;
    }

    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
