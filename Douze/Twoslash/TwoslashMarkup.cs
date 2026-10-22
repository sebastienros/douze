using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Douze.CodeHover;

namespace Douze.Twoslash;

/// <summary>
/// The twoslash notations of a snippet, read the way twoslash reads them (its <c>findFlagNotations</c>,
/// <c>findCutNotations</c>, <c>findQueryMarkers</c> and <c>splitFiles</c>), so that the removed ranges, the queried
/// positions and the files are the same:
/// <list type="bullet">
/// <item><c>// @name</c> and <c>// @name: value</c>: a compiler option (case insensitive, tsconfig names), a handbook option
/// (<c>errors</c>, <c>noErrors</c>, <c>noErrorsCutted</c>, <c>noErrorValidation</c>, <c>noStaticSemanticInfo</c>,
/// <c>keepNotations</c>, <c>showEmit</c>), a custom tag, or unknown; the line is removed in every case.</item>
/// <item><c>// ---cut---</c> (or <c>---cut-before---</c>), <c>// ---cut-after---</c>, <c>// ---cut-start---</c> and
/// <c>// ---cut-end---</c>: removed ranges.</item>
/// <item><c>// ^?</c> (quick info), <c>// ^|</c> (completions) and <c>// ^^^ text</c> (highlight): about the line above,
/// skipping lines that are markers themselves; the marker line is removed.</item>
/// <item><c>// @filename: name</c>: starts a file (the line is kept, as twoslash does).</item>
/// </list>
/// Code is expected with <c>\n</c> line ends.
/// </summary>
internal sealed partial class TwoslashMarkup
{
    public static readonly IReadOnlyList<string> HandbookOptionNames =
        ["errors", "noErrors", "noErrorsCutted", "noErrorValidation", "noStaticSemanticInfo", "showEmit", "showEmittedFile", "keepNotations"];

    private TwoslashMarkup(string code, string extension)
    {
        Code = code;
        Extension = extension;
    }

    public string Code { get; }

    /// <summary>
    /// <c>ts</c>, <c>tsx</c>, <c>js</c>, <c>jsx</c> or <c>json</c>: the extension of the default file.
    /// </summary>
    public string Extension { get; }

    /// <summary>
    /// The compiler options of <c>// @name: value</c> notations, by tsconfig name, as JSON values.
    /// </summary>
    public SortedDictionary<string, string> CompilerOptions { get; } = new(StringComparer.Ordinal);

    public HandbookOptions Handbook { get; private set; } = new();

    /// <summary>
    /// The ranges to remove from the code, start inclusive, end exclusive.
    /// </summary>
    public List<(int Start, int End)> Removals { get; } = [];

    public List<int> Queries { get; } = [];

    public List<int> Completions { get; } = [];

    public List<(int Start, int End, string? Text)> Highlights { get; } = [];

    /// <summary>
    /// The custom tags (<c>// @log: text</c>): nodes of type <c>tag</c>.
    /// </summary>
    public List<TwoslashNode> Tags { get; } = [];

    /// <summary>
    /// The notations that are neither compiler nor handbook options nor tags.
    /// </summary>
    public List<string> UnknownFlags { get; } = [];

    public List<SnippetFile> Files { get; } = [];

    /// <summary>
    /// Reads the notations of <paramref name="code"/>.
    /// </summary>
    /// <param name="code">The snippet, with <c>\n</c> line ends.</param>
    /// <param name="language">The fence language: <c>ts</c>, <c>typescript</c>, <c>tsx</c>, <c>js</c>, ...</param>
    /// <param name="customTags">The names of the notations that are tags rather than options.</param>
    /// <param name="handbook">The handbook options of the step, before the snippet's own.</param>
    /// <exception cref="TwoslashException">A notation is invalid (an unknown value, mismatched cut markers).</exception>
    public static TwoslashMarkup Parse(string code, string language, IReadOnlyCollection<string> customTags, HandbookOptions handbook)
    {
        var markup = new TwoslashMarkup(code, ExtensionOf(language)) { Handbook = handbook };
        markup.ReadFlags(customTags);
        markup.ReadCuts();
        markup.ReadQueries();
        markup.SplitFiles();
        return markup;
    }

    /// <summary>
    /// The extension of a fence language, as twoslash's <c>typesToExtension</c>.
    /// </summary>
    /// <exception cref="TwoslashException">The language is not one twoslash knows.</exception>
    public static string ExtensionOf(string language) => language switch
    {
        "js" or "javascript" or "mjs" or "cjs" => "js",
        "ts" or "typescript" or "mts" or "cts" => "ts",
        "tsx" => "tsx",
        "jsx" => "jsx",
        "json" or "jsn" or "map" => "json",
        _ => throw new TwoslashException($"unknown TypeScript extension '{language}'"),
    };

    /// <summary>
    /// Whether <paramref name="index"/> is inside a removed range (ends excluded) or outside the code.
    /// </summary>
    public bool IsInRemoval(int index)
    {
        if (index >= Code.Length || index < 0)
        {
            return true;
        }

        foreach (var (start, end) in Removals)
        {
            if (start < index && index < end)
            {
                return true;
            }
        }

        return false;
    }

    // The regular expressions of twoslash, with ECMAScript semantics (ASCII \w, \s with line breaks).
    [GeneratedRegex(@"^//\s?@(\w+)$", RegexOptions.Multiline | RegexOptions.ECMAScript)]
    private static partial Regex ConfigBoolean();

    [GeneratedRegex(@"^//\s?@(\w+):\s?([^\n\r]+)$", RegexOptions.Multiline | RegexOptions.ECMAScript)]
    private static partial Regex ConfigValue();

    [GeneratedRegex(@"^\s*//\s*\^(\?|\||\^+)( [^\n\r]*)?$", RegexOptions.Multiline | RegexOptions.ECMAScript)]
    private static partial Regex AnnotationMarker();

    [GeneratedRegex(@"^//\s?---cut(-before)?---$", RegexOptions.ECMAScript)]
    private static partial Regex CutBefore();

    [GeneratedRegex(@"^//\s?---cut-after---$", RegexOptions.ECMAScript)]
    private static partial Regex CutAfter();

    [GeneratedRegex(@"^//\s?---cut-start---$", RegexOptions.ECMAScript)]
    private static partial Regex CutStart();

    [GeneratedRegex(@"^//\s?---cut-end---$", RegexOptions.ECMAScript)]
    private static partial Regex CutEnd();

    [GeneratedRegex(@"^[\t\v\f ]*//\s?@filename: ([^\n\r]+)$", RegexOptions.Multiline | RegexOptions.ECMAScript)]
    private static partial Regex FilenameMarker();

    private void ReadFlags(IReadOnlyCollection<string> customTags)
    {
        var handbook = Handbook;

        foreach (Match match in ConfigBoolean().Matches(Code))
        {
            ReadFlag(match.Groups[1].Value, null, match.Index, match.Index + match.Length + 1, customTags, ref handbook);
        }

        foreach (Match match in ConfigValue().Matches(Code))
        {
            var name = match.Groups[1].Value;

            if (name == "filename")
            {
                continue;
            }

            ReadFlag(name, match.Groups[2].Value, match.Index, match.Index + match.Length + 1, customTags, ref handbook);
        }

        Handbook = handbook;
    }

    private void ReadFlag(string name, string? value, int start, int end, IReadOnlyCollection<string> customTags, ref HandbookOptions handbook)
    {
        Removals.Add((start, end));

        if (customTags.Contains(name))
        {
            Tags.Add(new TwoslashNode(TwoslashNodeType.Tag, end, 0) { Name = name, Text = value });
            return;
        }

        if (CompilerOptionTable.TryGet(name, out var option))
        {
            CompilerOptions[option.Name] = ToJson(option, value);
            return;
        }

        if (HandbookOptionNames.Contains(name))
        {
            handbook = handbook.With(name, value);
            return;
        }

        UnknownFlags.Add(name);
    }

    /// <summary>
    /// The JSON value of a compiler option notation, as twoslash's <c>parseFlag</c> reads it, written in the tsconfig
    /// form (enumerations and libraries by name).
    /// </summary>
    private static string ToJson(CompilerOptionTable.Option option, string? value)
    {
        switch (option.Kind)
        {
            case CompilerOptionTable.Kind.Boolean:
                return value == null || value.Length == 0 || value.Equals("true", StringComparison.OrdinalIgnoreCase) ? "true" : "false";

            case CompilerOptionTable.Kind.Number:
                if (value != null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    return number.ToString("R", CultureInfo.InvariantCulture);
                }

                throw new TwoslashException($"the compiler option '{option.Name}' needs a number, got '{value}'");

            case CompilerOptionTable.Kind.List:
                return JsonArray((value ?? "").Split(',').Select(x => x.Trim()));

            case CompilerOptionTable.Kind.Object:
                try
                {
                    using var document = JsonDocument.Parse(value ?? "");
                    return document.RootElement.GetRawText();
                }
                catch (JsonException e)
                {
                    throw new TwoslashException($"the compiler option '{option.Name}' needs a JSON object: {e.Message}");
                }

            default:
                if (option.Values != null && !option.Values.Contains((value ?? "").Trim().ToLowerInvariant()))
                {
                    throw new TwoslashException($"'{value}' is not a value of the compiler option '{option.Name}': {string.Join(", ", option.Values)}");
                }

                return JsonString((value ?? "").Trim());
        }
    }

    private void ReadCuts()
    {
        var removals = new List<(int, int)>();
        var cutStarts = new Stack<int>();
        var lines = Code.Split('\n');
        var index = 0;

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var comment = TrimJs(line);

            if (CutBefore().IsMatch(comment))
            {
                removals = [(0, index + line.Length + 1)];
            }
            else if (CutAfter().IsMatch(comment))
            {
                removals.Add((index, Code.Length));
                break;
            }
            else if (CutStart().IsMatch(comment))
            {
                cutStarts.Push(index);
            }
            else if (CutEnd().IsMatch(comment))
            {
                if (!cutStarts.TryPop(out var start))
                {
                    throw new TwoslashException($"mismatched cut markers: the cut-end at line {lineIndex + 1} has no cut-start before it");
                }

                removals.Add((start, index + line.Length + 1));
            }

            index += line.Length + 1;
        }

        if (cutStarts.Count > 0)
        {
            throw new TwoslashException("mismatched cut markers: a cut-start has no cut-end");
        }

        Removals.AddRange(removals);
    }

    private void ReadQueries()
    {
        if (!Code.Contains("//", StringComparison.Ordinal))
        {
            return;
        }

        var positions = new PositionConverter(Code);
        var queriedLines = new HashSet<int>();

        foreach (Match match in AnnotationMarker().Matches(Code))
        {
            var kind = match.Groups[1].Value;
            Removals.Add((match.Index, match.Index + match.Length + 1));

            var markerIndex = match.Value.IndexOf('^', StringComparison.Ordinal);
            var (line, character) = positions.ToPosition(match.Index + markerIndex);
            var targetLine = line - 1;

            while (queriedLines.Contains(targetLine) && targetLine >= 0)
            {
                targetLine--;
            }

            var target = positions.ToIndex(targetLine, character);

            if (kind == "?")
            {
                Queries.Add(target);
            }
            else if (kind == "|")
            {
                Completions.Add(target);
            }
            else
            {
                var length = match.Value.LastIndexOf('^') - markerIndex + 1;
                var text = match.Groups[2].Success ? TrimJs(match.Groups[2].Value) : null;
                Highlights.Add((target, target + length, text));
            }

            queriedLines.Add(line);
        }
    }

    private void SplitFiles()
    {
        var defaultName = "index." + Extension;
        var matches = FilenameMarker().Matches(Code);
        var current = matches.Any(x => x.Groups[1].Value.TrimEnd() == defaultName) ? "__index__.ts" : defaultName;
        var index = 0;

        foreach (Match match in matches)
        {
            if (match.Index > index)
            {
                Files.Add(new SnippetFile(current, index, Code[index..match.Index]));
            }

            current = match.Groups[1].Value.TrimEnd();
            index = match.Index;
        }

        if (index < Code.Length)
        {
            Files.Add(new SnippetFile(current, index, Code[index..]));
        }
    }

    /// <summary>
    /// JavaScript's <c>String.prototype.trim</c>: white space and line terminators.
    /// </summary>
    private static string TrimJs(string text) => text.Trim(' ', '\t', '\n', '\r', '\v', '\f', '\u00a0', '\ufeff', '\u2028', '\u2029');

    internal static string JsonString(string value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStringValue(value);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static string JsonArray(IEnumerable<string> values) => "[" + string.Join(",", values.Select(JsonString)) + "]";
}

/// <summary>
/// A file of a snippet: the default <c>index.ts</c>, or one started by <c>// @filename:</c>. <see cref="Offset"/> is
/// where its content starts in the snippet.
/// </summary>
internal sealed record SnippetFile(string Name, int Offset, string Content)
{
    public string Extension => Name.LastIndexOf('.') is var dot and >= 0 ? Name[(dot + 1)..] : Name;

    /// <summary>
    /// Whether the language service reads it: script files, and JSON with <c>resolveJsonModule</c>.
    /// </summary>
    public bool IsChecked(bool resolveJsonModule) => Extension is "ts" or "tsx" or "js" or "jsx" or "mts" or "cts" or "mjs" or "cjs" || (Extension == "json" && resolveJsonModule);
}

/// <summary>
/// The handbook options of twoslash: how errors are validated and what is reported.
/// </summary>
internal sealed record HandbookOptions
{
    /// <summary>
    /// The error codes a snippet expects (<c>// @errors: 2322 2345</c>).
    /// </summary>
    public IReadOnlyList<int> Errors { get; init; } = [];

    /// <summary>
    /// No error is reported (<c>// @noErrors</c>).
    /// </summary>
    public bool NoErrors { get; init; }

    /// <summary>
    /// The error codes that are not reported (<c>// @noErrors: 2322</c>).
    /// </summary>
    public IReadOnlyList<int> IgnoredErrors { get; init; } = [];

    /// <summary>
    /// Errors in removed ranges are not reported.
    /// </summary>
    public bool NoErrorsCutted { get; init; }

    /// <summary>
    /// Errors that <see cref="Errors"/> does not list do not fail the snippet.
    /// </summary>
    public bool NoErrorValidation { get; init; }

    /// <summary>
    /// No hover information.
    /// </summary>
    public bool NoStaticSemanticInfo { get; init; }

    /// <summary>
    /// The notations are kept in the code.
    /// </summary>
    public bool KeepNotations { get; init; }

    /// <summary>
    /// The emitted JavaScript is shown instead of the code: not supported.
    /// </summary>
    public bool ShowEmit { get; init; }

    /// <summary>
    /// A stable text for cache keys.
    /// </summary>
    public string Key => $"{string.Join(' ', Errors)}|{NoErrors}|{string.Join(' ', IgnoredErrors)}|{NoErrorsCutted}|{NoErrorValidation}|{NoStaticSemanticInfo}|{KeepNotations}|{ShowEmit}";

    /// <summary>
    /// These options with the handbook notation <paramref name="name"/> (<paramref name="value"/> null for
    /// <c>// @name</c>) applied.
    /// </summary>
    /// <exception cref="TwoslashException">The value is not a number list where one is needed.</exception>
    public HandbookOptions With(string name, string? value)
    {
        var flag = value == null || !value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);

        return name switch
        {
            "errors" => this with { Errors = Codes(value) },
            "noErrors" when value != null && value.Trim() is not ("true" or "false") => this with { NoErrors = false, IgnoredErrors = Codes(value) },
            "noErrors" => this with { NoErrors = flag, IgnoredErrors = [] },
            "noErrorsCutted" => this with { NoErrorsCutted = flag },
            "noErrorValidation" => this with { NoErrorValidation = flag },
            "noStaticSemanticInfo" => this with { NoStaticSemanticInfo = flag },
            "keepNotations" => this with { KeepNotations = flag },
            "showEmit" => this with { ShowEmit = flag },
            _ => this,
        };

        static int[] Codes(string? value)
        {
            var codes = new List<int>();

            foreach (var part in (value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
                {
                    throw new TwoslashException($"'{part}' is not an error code");
                }

                codes.Add(code);
            }

            return [.. codes];
        }
    }
}

/// <summary>
/// A snippet that twoslash would reject: an invalid notation, an unexpected error, a query that finds nothing.
/// </summary>
internal sealed class TwoslashException(string message) : Exception(message);
