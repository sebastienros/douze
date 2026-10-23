using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Douze.Highlighting;

/// <summary>
/// A line of a rendered code block: its HTML (the highlighted code, its markers and annotations), its text, its
/// number, its line marker (<c>mark</c>, <c>ins</c>, <c>del</c>) and the marker's label, and its indentation.
/// </summary>
internal sealed record CodeLine(string Html, string Text, int Number, string? Marker, string? Label, int Indent);

/// <summary>
/// A collapsed section of lines (1-based, inclusive), how it collapses (<c>github</c>, <c>collapsible-start</c>,
/// <c>collapsible-end</c>) and the indentation of its least indented line.
/// </summary>
internal sealed record CodeSection(int From, int To, string Style, int Indent);

/// <summary>
/// A code block ready for its template.
/// </summary>
internal sealed record CodeBlockModel(
    string Language,
    string Grammar,
    CodeMeta Meta,
    string? Title,
    string Frame,
    string Copy,
    bool Wrap,
    bool LineNumbers,
    IReadOnlyList<CodeLine> Lines,
    IReadOnlyList<CodeSection> Sections,
    int MaxLineLength,
    bool Popup);

/// <summary>
/// A twoslash node for the popup template: <c>hover</c>, <c>query</c>, <c>error</c>, <c>highlight</c>, <c>tag</c> or
/// <c>completion</c>; for a hover or a query, the type (a rendered code block), the documentation and the JSDoc tags
/// (HTML); for an error, its message, code and level; the HTML it wraps (the hovered code), and the column it starts at.
/// </summary>
internal sealed record PopupModel(
    string Kind,
    string Content,
    string? Type,
    string? Docs,
    IReadOnlyList<(string Name, string? Html, bool Dash)> Tags,
    string? Text,
    string? Code,
    string? Level,
    string? Name,
    int Character,
    IReadOnlyList<(string Name, string Kind, bool Deprecated)> Completions,
    string? CompletionsPrefix);

/// <summary>
/// The settings of the code blocks of a build.
/// </summary>
internal sealed class CodeBlockSettings
{
    /// <summary>
    /// The languages rendered in a terminal frame unless a fence says otherwise.
    /// </summary>
    public static readonly HashSet<string> TerminalLanguages = new(
        ["ansi", "bash", "bat", "batch", "cmd", "console", "fish", "nu", "nushell", "powershell", "ps", "ps1", "psd1", "psm1", "sh", "shell", "shellscript", "shellsession", "zsh"],
        StringComparer.Ordinal);

    /// <summary>
    /// The languages whose placeholders (<c>&lt;name&gt;</c>) are tokenized as words.
    /// </summary>
    public static readonly HashSet<string> ShellLanguages = new(["shellscript", "shell", "bash", "sh", "zsh", "nu", "nushell"], StringComparer.Ordinal);

    public required IReadOnlyList<CodeTheme> Themes { get; init; }

    /// <summary>
    /// The background of each marker type in each theme, which the colors of the text they mark are made readable on.
    /// </summary>
    public required IReadOnlyDictionary<string, Color[]> MarkerBackgrounds { get; init; }

    public double MinContrast { get; init; }

    /// <summary>
    /// Whether a file name in a comment on one of the first lines becomes the title (and the line is removed).
    /// </summary>
    public bool TitleFromComment { get; init; } = true;

    /// <summary>
    /// Whether the code to copy of a terminal block leaves its comment lines out.
    /// </summary>
    public bool CopyTerminalWithoutComments { get; init; } = true;

    /// <summary>
    /// The default of <c>showLineNumbers</c>.
    /// </summary>
    public bool LineNumbers { get; init; }

    /// <summary>
    /// The default marker backgrounds: CIE LCH colors of the marker's hue at a fixed chroma, lighter on light themes,
    /// half transparent.
    /// </summary>
    public static Dictionary<string, Color[]> DefaultMarkerBackgrounds(IReadOnlyList<CodeTheme> themes)
    {
        var hues = new Dictionary<string, int>(StringComparer.Ordinal) { ["mark"] = 284, ["ins"] = 136, ["del"] = 33 };
        return hues.ToDictionary(x => x.Key, x => themes.Select(t => Color.Parse($"lch({(t.Type == "dark" ? 32 : 75)}% 40 {x.Value} / 50%)")).ToArray(), StringComparer.Ordinal);
    }
}

/// <summary>
/// Builds the model of a code block from a fence: its language and meta options, its lines, their tokens, markers and
/// twoslash annotations. The options are those of Markdown highlighters for the web:
/// <list type="bullet">
/// <item><c>title="..."</c>, else a file name in a comment on one of the first four lines (the line is removed);</item>
/// <item><c>frame=code|terminal|none|auto</c> (terminal languages get a terminal frame, unless the title is a file
/// name or the code starts with <c>#!</c>);</item>
/// <item>line markers: <c>{1,3-5}</c> (or <c>mark=</c>), <c>ins=</c>, <c>del=</c> (also <c>add=</c>, <c>rem=</c>),
/// with labels (<c>{"A":2-3}</c>); inline markers: <c>"text"</c>, <c>/regex/</c> (marking its groups when it has
/// some), keyed the same way; <c>diff</c> fences (and <c>lang=</c> to highlight them in that language, or
/// <c>useDiffSyntax</c>);</item>
/// <item><c>collapse={1-5}</c> and <c>collapseStyle=github|collapsible-start|collapsible-end|collapsible-auto</c>,
/// <c>collapsePreserveIndent</c>;</item>
/// <item><c>showLineNumbers</c>, <c>startLineNumber=</c>, <c>wrap</c>, <c>preserveIndent</c>, <c>hangingIndent=</c>.</item>
/// </list>
/// </summary>
internal sealed partial class CodeBlockBuilder(CodeBlockSettings settings, Func<string, string, ValueTask<List<Token>[]>> tokenize, Func<PopupModel, ValueTask<string>> renderPopup, Func<string, string, ValueTask<string>> renderType, Func<string, ValueTask<string>> renderDocs)
{
    private static readonly string[] MarkerTypes = ["mark", "del", "ins"];

    public async ValueTask<CodeBlockModel> BuildAsync(string code, string language, string meta, string? twoslash, bool popup = false)
    {
        var options = new CodeMeta(meta);
        var lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(x => x.TrimEnd()).ToList();

        while (lines.Count > 0 && lines[0].Length == 0)
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var useDiff = false;

        if (language == "diff" && options.GetString("lang") is { } diffLanguage)
        {
            language = diffLanguage;
            useDiff = true;
        }

        useDiff = options.GetBoolean("useDiffSyntax") ?? useDiff;

        var title = options.GetString("title");
        var frame = options.GetString("frame") is { } frameOption ? FrameType(frameOption) : "auto";
        var wrap = options.GetBoolean("wrap") ?? false;
        var preserveIndent = options.GetBoolean("preserveIndent") ?? true;
        var hangingIndent = options.GetInteger("hangingIndent") ?? 0;
        var lineNumbers = !popup && (options.GetBoolean("showLineNumbers") ?? settings.LineNumbers);
        var startLineNumber = options.GetInteger("startLineNumber") ?? 1;

        // Line markers: by line object, so that they follow lines removed above them.
        var lineObjects = lines.Select(x => new LineState(x)).ToList();
        var inlineDefinitions = new List<(string Type, string? Text, Regex? Regex)>();

        foreach (var option in options.List(["", "mark", "ins", "del", "add", "rem"]))
        {
            var type = MarkerType(option.Key.Length == 0 ? "mark" : option.Key.ToLowerInvariant());

            switch (option.Kind)
            {
                case MetaKind.String:
                    inlineDefinitions.Add((type, option.Value, null));
                    break;
                case MetaKind.Regex:
                    if (TryRegex(option.Value) is { } regex)
                    {
                        inlineDefinitions.Add((type, null, regex));
                    }

                    break;
                case MetaKind.Range:
                    string? label = null;
                    var range = LabelPrefix().Replace(option.Value, m =>
                    {
                        label = m.Groups[2].Value;
                        return "";
                    }, 1);
                    var numbers = ParseRange(range);

                    for (var i = 0; i < numbers.Count; i++)
                    {
                        if (numbers[i] - 1 is var index && index >= 0 && index < lineObjects.Count)
                        {
                            lineObjects[index].Markers.Add((type, i == 0 || numbers[i] - numbers[i - 1] != 1 ? label : null));
                        }
                    }

                    break;
            }
        }

        // Collapsed sections, by line object.
        var collapseStyle = options.GetString("collapseStyle")?.ToLowerInvariant() is "collapsible-start" or "collapsible-end" or "collapsible-auto" or "github"
            ? options.GetString("collapseStyle")!.ToLowerInvariant()
            : "github";
        var sections = ParseSections(string.Join(',', options.GetRanges("collapse")))
            .Select(x => lineObjects.Skip(x.From - 1).Take(x.To - x.From + 1).ToList())
            .Where(x => x.Count > 0)
            .ToList();

        // Diff syntax: +/- at the start of lines are markers, unless the code looks like a real diff file.
        if (language == "diff" || useDiff)
        {
            var realDiff = lineObjects.Take(4).Any(x => RealDiffLine().IsMatch(x.Text));

            if (!realDiff)
            {
                var parsed = lineObjects.Select(x => (Line: x, Match: DiffLine().Match(x.Text))).ToList();
                var minIndent = parsed.Where(x => x.Match.Groups[3].Value.Trim().Length > 0).Select(x => x.Match.Groups[1].Length).DefaultIfEmpty(int.MaxValue).Min();

                foreach (var (line, match) in parsed)
                {
                    var marker = match.Groups[2].Value switch { "+" => "ins", "-" => "del", _ => null };
                    var remove = minIndent is > 0 and < int.MaxValue ? minIndent : marker != null ? 1 : 0;

                    if (remove > 0)
                    {
                        line.Text = line.Text[Math.Min(remove, line.Text.Length)..];
                    }

                    if (marker != null)
                    {
                        line.Markers.Add((marker, null));
                    }
                }
            }
        }

        // The title from a comment of the first lines.
        if (title == null && frame != "none" && settings.TitleFromComment && !popup)
        {
            title = ExtractFileName(lineObjects, language);
        }

        if (frame == "auto" && CodeBlockSettings.TerminalLanguages.Contains(language))
        {
            var titleIsFileName = title != null && FileNameFromComment($"// {title}", language) != null;

            if (titleIsFileName || lineObjects.Take(4).Any(x => Shebang().IsMatch(x.Text)))
            {
                frame = "code";
            }
        }

        // Twoslash: the code without its notations replaces the lines, and the nodes annotate them.
        var nodes = new List<JsonElement>();
        JsonDocument? twoslashDocument = null;

        if (twoslash != null)
        {
            twoslashDocument = JsonDocument.Parse(twoslash);
            var twoslashLines = twoslashDocument.RootElement.GetProperty("code").GetString()!.Split('\n');

            for (var i = 0; i < twoslashLines.Length && i < lineObjects.Count; i++)
            {
                lineObjects[i].Text = twoslashLines[i];
            }

            if (twoslashLines.Length < lineObjects.Count)
            {
                lineObjects.RemoveRange(twoslashLines.Length, lineObjects.Count - twoslashLines.Length);
            }

            nodes.AddRange(twoslashDocument.RootElement.GetProperty("nodes").EnumerateArray());
        }

        using var _ = twoslashDocument;

        // Tokens, of the code as it now is.
        var text = string.Join('\n', lineObjects.Select(x => x.Text));
        var tokenized = CodeBlockSettings.ShellLanguages.Contains(language) ? Placeholder().Replace(text, "X$1X") : text;
        var tokens = await tokenize(tokenized, language);
        var resolvedFrame = frame == "auto" ? (CodeBlockSettings.TerminalLanguages.Contains(language) ? "terminal" : "code") : frame;

        // Lines.
        var rendered = new List<CodeLine>(lineObjects.Count);

        for (var i = 0; i < lineObjects.Count; i++)
        {
            var line = lineObjects[i];
            var marker = line.Markers.Count == 0 ? default : line.Markers.MaxBy(x => Array.IndexOf(MarkerTypes, x.Type));
            var html = await RenderLineAsync(line.Text, i, tokens[i], marker.Type, inlineDefinitions, nodes, language);
            var indent = wrap && (preserveIndent || hangingIndent > 0) ? (preserveIndent ? LeadingSpaces(line.Text) : 0) + hangingIndent : 0;
            rendered.Add(new CodeLine(html, line.Text, i + startLineNumber, marker.Type, marker.Type == null ? null : line.Markers.Where(x => x.Type == marker.Type).Select(x => x.Label).FirstOrDefault(x => x != null), indent));
        }

        var modelSections = new List<CodeSection>();

        foreach (var section in sections)
        {
            var indices = section.Select(x => lineObjects.IndexOf(x)).Where(x => x >= 0).ToList();

            if (indices.Count == 0)
            {
                continue;
            }

            var from = indices.Min() + 1;
            var to = indices.Max() + 1;
            var sectionIndent = options.GetBoolean("collapsePreserveIndent") is false
                ? 0
                : lineObjects.Skip(from - 1).Take(to - from + 1).Where(x => x.Text.Trim().Length > 0).Select(x => LeadingSpaces(x.Text)).DefaultIfEmpty(0).Min();
            var style = collapseStyle == "collapsible-auto" ? (to >= lineObjects.Count ? "collapsible-end" : "collapsible-start") : collapseStyle;
            modelSections.Add(new CodeSection(from, to, style, sectionIndent));
        }

        // The code to copy: without the comment lines of a terminal.
        var copy = text;

        if (settings.CopyTerminalWithoutComments && resolvedFrame == "terminal")
        {
            copy = TerminalComment().Replace(copy, "").Trim();
        }

        return new CodeBlockModel(
            language,
            CodeTokenizer.IsPlain(language) ? "" : language,
            options,
            title,
            resolvedFrame,
            copy,
            wrap,
            lineNumbers,
            rendered,
            [.. modelSections.OrderBy(x => x.From)],
            lineObjects.Count == 0 ? 0 : lineObjects.Max(x => x.Text.Length),
            popup);
    }

    private sealed class LineState(string text)
    {
        public string Text { get; set; } = text;
        public List<(string Type, string? Label)> Markers { get; } = [];
    }

    /// <summary>
    /// The HTML of a line: its tokens as spans with a CSS variable per theme (<c>--0:#color</c>, <c>--0fs:italic</c>,
    /// <c>--0fw:bold</c>, <c>--0td:underline</c>), its leading white space in <c>&lt;span class="indent"&gt;</c>, its inline
    /// markers (<c>&lt;mark&gt;</c>, <c>&lt;ins&gt;</c>, <c>&lt;del&gt;</c>, split at the boundaries of the annotations
    /// inside them: <c>open-start</c>, <c>open-end</c>) and its twoslash annotations (the popup template).
    /// </summary>
    private async ValueTask<string> RenderLineAsync(string text, int lineIndex, List<Token> lineTokens, string? lineMarker, List<(string Type, string? Text, Regex? Regex)> inlineDefinitions, List<JsonElement> nodes, string language)
    {
        // Inline markers, flattened: a later type wins where markers overlap, markers of a type merge.
        var markers = FlattenMarkers(FindInlineMarkers(text, inlineDefinitions));

        // Twoslash nodes of the line.
        var hovers = new List<(int Start, int End, JsonElement Node)>();
        var highlights = new List<(int Start, int End)>();
        var underlines = new List<(int Start, int End)>();
        var after = new List<JsonElement>();
        JsonElement? tag = null;
        var lineNodes = nodes.Where(x => x.GetProperty("line").GetInt32() == lineIndex).ToList();
        var errors = lineNodes.Where(x => Type(x) == "error").ToList();
        var queries = lineNodes.Where(x => Type(x) == "query").ToList();

        foreach (var node in lineNodes)
        {
            var start = Int(node, "character");
            var end = start + Int(node, "length");

            switch (Type(node))
            {
                case "error":
                    underlines.Add((start, end));
                    after.Add(node);
                    break;
                case "query":
                    after.Add(node);
                    break;
                case "highlight":
                    highlights.Add((start, end));
                    break;
                case "hover":
                    // A hover of a query or an error of the same place is theirs.
                    if (queries.Any(q => String(q, "text") == String(node, "text")) || errors.Any(e => Int(e, "character") == start && Int(e, "length") == end - start))
                    {
                        continue;
                    }

                    hovers.Add((start, end, node));
                    break;
                case "tag":
                    tag = node;
                    break;
                case "completion":
                    after.Add(node);
                    break;
            }
        }

        var indent = LeadingSpaces(text);

        // The styles of the segments: each theme's token style, readable on the markers' backgrounds.
        var boundaries = new SortedSet<int> { 0, text.Length, indent };

        foreach (var token in lineTokens)
        {
            boundaries.Add(token.Start);
            boundaries.Add(token.End);
        }

        foreach (var (start, end, _) in markers)
        {
            boundaries.Add(start);
            boundaries.Add(end);
        }

        foreach (var range in hovers.Select(x => (x.Start, x.End)).Concat(highlights).Concat(underlines))
        {
            boundaries.Add(Math.Clamp(range.Start, 0, text.Length));
            boundaries.Add(Math.Clamp(range.End, 0, text.Length));
        }

        var points = boundaries.Where(x => x >= 0 && x <= text.Length).ToArray();
        var layers = new List<Layer>
        {
            new("marker", [.. markers.Select(x => new LayerRange(x.Start, x.End, x.Type))]),
            new("highlight", [.. NonOverlapping(highlights).Select(x => new LayerRange(x.Start, x.End, null))]),
            new("underline", [.. NonOverlapping(underlines).Select(x => new LayerRange(x.Start, x.End, null))]),
            new("hover", [.. NonOverlapping(hovers.Select(x => (x.Start, x.End))).Select(x => new LayerRange(x.Start, x.End, hovers.First(h => h.Start == x.Start && h.End == x.End).Node))]),
            new("indent", indent > 0 ? [new LayerRange(0, indent, null)] : []),
        };

        var builder = new StringBuilder();
        await RenderRangeAsync(builder, text, 0, text.Length, 0, layers, points, lineTokens, lineMarker, markers, language);

        var html = builder.ToString();

        if (tag is { } tagNode)
        {
            html = await renderPopup(new PopupModel("tag", html, null, null, [], String(tagNode, "text"), null, null, String(tagNode, "name"), 0, [], null));
        }

        foreach (var node in after)
        {
            html += Type(node) switch
            {
                "error" => await renderPopup(new PopupModel("error", "", null, null, [], String(node, "text"), ErrorCode(node), String(node, "level") ?? "error", null, Int(node, "character"), [], null)),
                "query" => await RenderInfoAsync("query", node, "", language),
                _ => await renderPopup(new PopupModel("completion", "", null, null, [], null, null, null, null, Int(node, "character"), Completions(node), String(node, "completionsPrefix"))),
            };
        }

        return html;
    }

    private sealed record LayerRange(int Start, int End, object? Data);

    private sealed record Layer(string Kind, List<LayerRange> Ranges);

    private async ValueTask RenderRangeAsync(StringBuilder builder, string text, int start, int end, int layerIndex, List<Layer> layers, int[] points, List<Token> tokens, string? lineMarker, List<(int Start, int End, string Type)> markers, string language)
    {
        if (start >= end)
        {
            return;
        }

        if (layerIndex == layers.Count)
        {
            AppendTokens(builder, text, start, end, points, tokens, lineMarker, markers);
            return;
        }

        var layer = layers[layerIndex];
        var position = start;

        foreach (var range in layer.Ranges.Where(x => x.Start < end && x.End > start).OrderBy(x => x.Start))
        {
            var rangeStart = Math.Max(range.Start, start);
            var rangeEnd = Math.Min(range.End, end);

            if (rangeStart < position)
            {
                continue;
            }

            await RenderRangeAsync(builder, text, position, rangeStart, layerIndex + 1, layers, points, tokens, lineMarker, markers, language);

            var inner = new StringBuilder();
            await RenderRangeAsync(inner, text, rangeStart, rangeEnd, layerIndex + 1, layers, points, tokens, lineMarker, markers, language);

            switch (layer.Kind)
            {
                case "marker":
                    var classes = new List<string>();

                    if (rangeStart > range.Start)
                    {
                        classes.Add("open-start");
                    }

                    if (rangeEnd < range.End)
                    {
                        classes.Add("open-end");
                    }

                    var type = (string)range.Data!;
                    builder.Append('<').Append(type);

                    if (classes.Count > 0)
                    {
                        builder.Append(" class=\"").Append(string.Join(' ', classes)).Append('"');
                    }

                    builder.Append('>').Append(inner).Append("</").Append(type).Append('>');
                    break;
                case "indent":
                    builder.Append("<span class=\"indent\">").Append(Html.Encode(text.AsSpan(rangeStart, rangeEnd - rangeStart))).Append("</span>");
                    break;
                case "highlight":
                    builder.Append(await renderPopup(new PopupModel("highlight", inner.ToString(), null, null, [], null, null, null, null, rangeStart, [], null)));
                    break;
                case "underline":
                    builder.Append(await renderPopup(new PopupModel("underline", inner.ToString(), null, null, [], null, null, null, null, rangeStart, [], null)));
                    break;
                case "hover":
                    builder.Append(await RenderInfoAsync("hover", (JsonElement)range.Data!, inner.ToString(), language));
                    break;
            }

            position = rangeEnd;
        }

        await RenderRangeAsync(builder, text, position, end, layerIndex + 1, layers, points, tokens, lineMarker, markers, language);
    }

    /// <summary>
    /// The segments of [start, end) as spans of their styles; white space alone in the indentation has none.
    /// </summary>
    private void AppendTokens(StringBuilder builder, string text, int start, int end, int[] points, List<Token> tokens, string? lineMarker, List<(int Start, int End, string Type)> markers)
    {
        var indent = LeadingSpaces(text);

        if (end <= indent)
        {
            builder.Append(Html.Encode(text.AsSpan(start, end - start)));
            return;
        }

        string? lastStyle = null;
        var run = new StringBuilder();

        void Flush()
        {
            if (run.Length == 0)
            {
                return;
            }

            if (lastStyle is { Length: > 0 })
            {
                builder.Append("<span style=\"").Append(lastStyle).Append("\">").Append(run).Append("</span>");
            }
            else
            {
                builder.Append(run);
            }

            run.Clear();
        }

        var index = Array.BinarySearch(points, start);

        for (var p = index < 0 ? ~index : index; p < points.Length - 1 && points[p] < end; p++)
        {
            var segmentStart = Math.Max(points[p], start);
            var segmentEnd = Math.Min(points[p + 1], end);

            if (segmentEnd <= segmentStart)
            {
                continue;
            }

            var style = StyleAt(segmentStart, tokens, lineMarker, markers, text.Length);

            if (style != lastStyle)
            {
                Flush();
                lastStyle = style;
            }

            run.Append(Html.Encode(text.AsSpan(segmentStart, segmentEnd - segmentStart)));
        }

        Flush();
    }

    /// <summary>
    /// The style attribute of the character at <paramref name="index"/>: per theme, the token's color (made readable
    /// on the marker under it) and font style.
    /// </summary>
    private string StyleAt(int index, List<Token> tokens, string? lineMarker, List<(int Start, int End, string Type)> markers, int length)
    {
        var token = tokens.FirstOrDefault(x => x.Start <= index && x.End > index);

        if (token.Styles == null)
        {
            return "";
        }

        var inlineMarker = markers.FirstOrDefault(x => x.Start <= index && x.End > index).Type;
        var builder = new StringBuilder();

        for (var t = 0; t < token.Styles.Length; t++)
        {
            var style = token.Styles[t];
            var color = style.Color;

            if ((lineMarker != null || inlineMarker != null) && settings.MinContrast > 0)
            {
                var theme = settings.Themes[t];
                var background = Color.Parse(theme.Background);
                var lineBackground = (lineMarker != null ? settings.MarkerBackgrounds[lineMarker][t] : new Color(0, 0, 0, 0)).OnBackground(background);
                var combined = (inlineMarker != null ? settings.MarkerBackgrounds[inlineMarker][t] : new Color(0, 0, 0, 0)).OnBackground(lineBackground);

                if (Color.TryParse(color, out var parsed))
                {
                    color = Color.EnsureContrast(parsed, combined, settings.MinContrast);
                }
            }

            if (builder.Length > 0)
            {
                builder.Append(';');
            }

            builder.Append("--").Append(t).Append(':').Append(color.ToUpperInvariant());

            if (style.Italic)
            {
                builder.Append(";--").Append(t).Append("fs:italic");
            }

            if (style.Bold)
            {
                builder.Append(";--").Append(t).Append("fw:bold");
            }

            if (style.Underline || style.Strikethrough)
            {
                builder.Append(";--").Append(t).Append("td:").Append(style.Underline && style.Strikethrough ? "underline line-through" : style.Underline ? "underline" : "line-through");
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// A hover or a query: its type highlighted (TypeScript's quick info cleaned up as twoslash renderers do), its
    /// documentation and its JSDoc tags.
    /// </summary>
    private async ValueTask<string> RenderInfoAsync(string kind, JsonElement node, string content, string language)
    {
        var info = String(node, "text") ?? "";
        var typescript = language is "ts" or "tsx" or "typescript" or "js" or "jsx" or "javascript" or "mts" or "cts" or "vue";
        var type = await renderType(typescript ? CleanTypeScriptInfo(info) : info, typescript ? "ts" : language);
        var docs = String(node, "docs") is { Length: > 0 } markdown ? await renderDocs(JsDocLink().Replace(markdown, "$1")) : null;
        var tags = new List<(string, string?, bool)>();

        if (node.TryGetProperty("tags", out var tagArray) && tagArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in tagArray.EnumerateArray())
            {
                var name = item[0].GetString()!;
                var value = item.GetArrayLength() > 1 && item[1].ValueKind == JsonValueKind.String ? item[1].GetString() : null;
                string? html = null;
                var dash = false;

                if (!string.IsNullOrEmpty(value))
                {
                    html = await renderDocs(JsDocLink().Replace(value, "$1"));
                    var single = SingleParagraph().Match(html);

                    if (single.Success)
                    {
                        html = single.Groups[1].Value;
                    }

                    // A dash between the name and the value, but for the tags of examples and descriptions.
                    dash = !JsDocBlockTag().IsMatch(name);
                }

                tags.Add((name, html, dash));
            }
        }

        return await renderPopup(new PopupModel(kind, content, type, docs, tags, null, null, null, null, Int(node, "character"), [], null));
    }

    /// <summary>
    /// TypeScript's quick info as a declaration to highlight: without <c>(property)</c> prefixes, imports and
    /// <c>interface X</c> lines, with <c>type </c> or <c>function </c> in front of an alias or a call signature.
    /// </summary>
    private static string CleanTypeScriptInfo(string info)
    {
        var content = InterfaceLine().Replace(ImportLine().Replace(LeadingKind().Replace(info, ""), ""), "").Trim();

        if (content.Length == 0)
        {
            return info;
        }

        return TypeAlias().IsMatch(content) ? "type " + content : FunctionCall().IsMatch(content) ? "function " + content : content;
    }

    private static string? Type(JsonElement node) => String(node, "type");

    private static string? String(JsonElement node, string name) => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Int(JsonElement node, string name) => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    private static string? ErrorCode(JsonElement node) => node.TryGetProperty("code", out var code)
        ? code.ValueKind switch { JsonValueKind.Number => code.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture), JsonValueKind.String => code.GetString(), _ => null }
        : null;

    private static List<(string, string, bool)> Completions(JsonElement node)
    {
        var result = new List<(string, string, bool)>();

        if (node.TryGetProperty("completions", out var completions) && completions.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in completions.EnumerateArray().Take(5))
            {
                result.Add((String(item, "name") ?? "", String(item, "kind") ?? "property", String(item, "kindModifiers")?.Split(',').Contains("deprecated") == true));
            }
        }

        return result;
    }

    private static List<(int Start, int End)> NonOverlapping(IEnumerable<(int Start, int End)> ranges)
    {
        var result = new List<(int, int)>();
        var last = -1;

        foreach (var range in ranges.Where(x => x.End > x.Start).OrderBy(x => x.Start).ThenByDescending(x => x.End))
        {
            if (range.Start >= last)
            {
                result.Add(range);
                last = range.End;
            }
        }

        return result;
    }

    private static List<(int Start, int End, string Type)> FindInlineMarkers(string text, List<(string Type, string? Text, Regex? Regex)> definitions)
    {
        var found = new List<(int, int, string)>();

        foreach (var type in MarkerTypes)
        {
            foreach (var (definitionType, search, regex) in definitions)
            {
                if (definitionType != type)
                {
                    continue;
                }

                if (search is { Length: > 0 })
                {
                    for (var index = text.IndexOf(search, StringComparison.Ordinal); index >= 0; index = text.IndexOf(search, index + search.Length, StringComparison.Ordinal))
                    {
                        found.Add((index, index + search.Length, type));
                    }
                }
                else if (regex != null)
                {
                    foreach (Match match in regex.Matches(text))
                    {
                        var groups = match.Groups.Cast<Group>().Skip(1).Where(x => x.Success).ToList();

                        if (groups.Count == 0)
                        {
                            found.Add((match.Index, match.Index + match.Length, type));
                        }
                        else
                        {
                            found.AddRange(groups.Select(x => (x.Index, x.Index + x.Length, type)));
                        }
                    }
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Inline marker ranges without overlaps: added by type (<c>mark</c>, <c>del</c>, <c>ins</c>), a new range removes
    /// the parts of earlier ones it covers, ranges of one type merge.
    /// </summary>
    private static List<(int Start, int End, string Type)> FlattenMarkers(List<(int Start, int End, string Type)> ranges)
    {
        var flat = new List<(int Start, int End, string Type)>();

        foreach (var type in MarkerTypes)
        {
            foreach (var range in ranges.Where(x => x.Type == type))
            {
                var current = range;

                for (var i = flat.Count - 1; i >= 0; i--)
                {
                    var existing = flat[i];

                    if (current.End <= existing.Start || current.Start >= existing.End)
                    {
                        continue;
                    }

                    if (current.Start <= existing.Start && current.End >= existing.End)
                    {
                        flat.RemoveAt(i);
                        continue;
                    }

                    if (current.Type == existing.Type)
                    {
                        flat.RemoveAt(i);
                        current = (Math.Min(current.Start, existing.Start), Math.Max(current.End, existing.End), current.Type);
                        continue;
                    }

                    if (current.Start > existing.Start && current.End < existing.End)
                    {
                        flat[i] = existing with { End = current.Start };
                        flat.Insert(i + 1, existing with { Start = current.End });
                        continue;
                    }

                    if (current.Start > existing.Start)
                    {
                        flat[i] = existing with { End = current.Start };
                    }

                    if (current.End < existing.End)
                    {
                        flat[i] = flat[i] with { Start = current.End };
                    }
                }

                flat.Add(current);
                flat.Sort((a, b) => a.Start.CompareTo(b.Start));
            }
        }

        return [.. flat.Where(x => x.End > x.Start)];
    }

    private static int LeadingSpaces(string text)
    {
        var i = 0;

        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        return i;
    }

    private static string FrameType(string value) => value switch
    {
        "" => "none",
        "editor" => "code",
        "shell" => "terminal",
        "code" or "terminal" or "none" or "auto" => value,
        _ => throw new DouzeException($"invalid frame type '{value}' in a code fence: code, terminal, none or auto"),
    };

    private static string MarkerType(string key) => key switch { "add" => "ins", "rem" => "del", _ => key };

    private static Regex? TryRegex(string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The line numbers of a range: <c>1, 3-5</c> (also <c>..</c>, <c>...</c>), as parse-numeric-range reads it.
    /// </summary>
    internal static List<int> ParseRange(string range)
    {
        var numbers = new List<int>();

        foreach (var part in range.Split(',').Select(x => x.Trim()))
        {
            if (IntegerRegex().IsMatch(part))
            {
                numbers.Add(int.Parse(part, System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (RangePart().Match(part) is { Success: true } match)
            {
                var from = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                var to = int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                var step = from < to ? 1 : -1;

                if (match.Groups[2].Value is "-" or ".." or "‥")
                {
                    to += step;
                }

                for (var i = from; i != to; i += step)
                {
                    numbers.Add(i);
                }
            }
        }

        return numbers;
    }

    private static List<(int From, int To)> ParseSections(string value)
    {
        var sections = new List<(int From, int To)>();

        foreach (var part in value.Split(','))
        {
            var bounds = part.Split('-');

            if (bounds.Length != 2 || !int.TryParse(bounds[0], out var from) || !int.TryParse(bounds[1], out var to) || from > to)
            {
                continue;
            }

            if (sections.Any(x => (from >= x.From && from <= x.To) || (to >= x.From && to <= x.To)))
            {
                continue;
            }

            sections.Add((from, to));
        }

        return sections;
    }

    /// <summary>
    /// A file name in a comment on one of the first four lines, which is removed (with an empty front matter left
    /// behind, and the empty line after it).
    /// </summary>
    private static string? ExtractFileName(List<LineState> lines, string language)
    {
        string? name = null;
        var index = -1;

        for (var i = 0; i < Math.Min(4, lines.Count) && name == null; i++)
        {
            name = FileNameFromComment(lines[i].Text, language);
            index = i;
        }

        if (name == null)
        {
            return null;
        }

        lines.RemoveAt(index);

        if (language is "astro" or "markdown" or "md" or "mdx" or "toml" or "yaml" or "yml")
        {
            var opening = index > 0 ? lines[index - 1].Text.Trim() : null;
            var closing = index < lines.Count ? lines[index].Text.Trim() : null;

            if (opening == closing && opening is "---" or "+++")
            {
                index--;
                lines.RemoveAt(index);
                lines.RemoveAt(index);
            }
        }

        if (index < lines.Count && index >= 0 && lines[index].Text.Trim().Length == 0)
        {
            lines.RemoveAt(index);
        }

        return name;
    }

    private static readonly Dictionary<string, string[]> LanguageGroups = new(StringComparer.Ordinal)
    {
        ["code"] = ["astro", "cjs", "htm", "html", "js", "jsx", "mjs", "svelte", "ts", "tsx", "typescript", "vb", "vue", "vue-html"],
        ["terminal"] = [.. CodeBlockSettings.TerminalLanguages.Order(StringComparer.Ordinal)],
        ["data"] = ["csv", "env", "ini", "json", "toml", "xml", "yaml", "yml"],
        ["styles"] = ["css", "less", "sass", "scss", "styl", "stylus", "xsl"],
        ["textContent"] = ["markdown", "md", "mdx"],
    };

    private static readonly Regex FileNameComment = new(
        @"^\s*(?://|#(?!!)|<!--|/\*)\s*(?:((?![a-z]:).*?)(?:：|:(?!//)))?\s*((?:[a-z]:)?[\w./~%[\]+\\-]*(?:\.(?:"
        + string.Join('|', LanguageGroups.Values.SelectMany(x => x).Order(StringComparer.Ordinal))
        + @"))?)\s*(?:-->|\*/)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The file name of a comment such as <c>// src/app.ts</c> or <c># file: config.yml</c>, when it looks like one for
    /// the language.
    /// </summary>
    private static string? FileNameFromComment(string line, string language)
    {
        var match = FileNameComment.Match(line);
        var prefix = match.Groups[1].Value;
        var name = match.Success ? match.Groups[2].Value : "";

        if (name.Length == 0 || prefix.Trim().Equals("@filename", StringComparison.OrdinalIgnoreCase) || !NotOnlyPathCharacters().IsMatch(name) || LeadingDots().IsMatch(name))
        {
            return null;
        }

        var group = LanguageGroups.Values.FirstOrDefault(x => x.Contains(language));
        var withoutPath = PathPrefix().Replace(name, "");
        var extension = Extension().Match(withoutPath) is { Success: true } e ? e.Groups[1].Value : null;
        var typicalStart = TypicalStart().IsMatch(name);
        var dotFile = withoutPath.StartsWith('.');
        var separatedPath = Separator().IsMatch(name) && NotPathOrNumbers().IsMatch(name) && !Whitespace().IsMatch(name) && name == name.ToLowerInvariant();

        if ((typicalStart || dotFile || separatedPath) && (prefix.Length == 0 || group == LanguageGroups["terminal"]))
        {
            return name;
        }

        if (extension == null || (group != null && !group.Contains(extension)))
        {
            return null;
        }

        return name;
    }

    [GeneratedRegex(@"^\s*?([""'])((?:(?!\1).)+?)\1:\s*?")]
    private static partial Regex LabelPrefix();

    [GeneratedRegex(@"^([*+-]{3}\s|@@\s|[0-9,]+[acd][0-9,]+\s*$)")]
    private static partial Regex RealDiffLine();

    [GeneratedRegex(@"^(([+-](?![+-]))?\s*)(.*)$")]
    private static partial Regex DiffLine();

    [GeneratedRegex(@"^\s*#!")]
    private static partial Regex Shebang();

    [GeneratedRegex(@"<([^>]*[^>\s])>")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"(?<=^|\n)\s*#.*($|\n+)")]
    private static partial Regex TerminalComment();

    [GeneratedRegex(@"\{@link ([^}]*)\}")]
    private static partial Regex JsDocLink();

    [GeneratedRegex(@"^<p>(.*)</p>\s*$", RegexOptions.Singleline)]
    private static partial Regex SingleParagraph();

    [GeneratedRegex(@"\b(example|description)\b")]
    private static partial Regex JsDocBlockTag();

    [GeneratedRegex(@"^\(([\w-]+)\)\s+", RegexOptions.Multiline)]
    private static partial Regex LeadingKind();

    [GeneratedRegex(@"\nimport .*$")]
    private static partial Regex ImportLine();

    [GeneratedRegex(@"^(interface|namespace) \w+$", RegexOptions.Multiline)]
    private static partial Regex InterfaceLine();

    [GeneratedRegex(@"^[A-Z]\w*(<[^>]*>)?:")]
    private static partial Regex TypeAlias();

    [GeneratedRegex(@"^\w*\(")]
    private static partial Regex FunctionCall();

    [GeneratedRegex(@"^-?\d+$")]
    private static partial Regex IntegerRegex();

    [GeneratedRegex(@"^(-?\d+)(-|\.\.\.?|‥|…|⋯)(-?\d+)$")]
    private static partial Regex RangePart();

    [GeneratedRegex(@"[^.:/\\~]")]
    private static partial Regex NotOnlyPathCharacters();

    [GeneratedRegex(@"^\.{2,}(?!/|\\)")]
    private static partial Regex LeadingDots();

    [GeneratedRegex(@"^.*[/\\]")]
    private static partial Regex PathPrefix();

    [GeneratedRegex(@"\.([^.]+)$")]
    private static partial Regex Extension();

    [GeneratedRegex(@"^(/|\\|\.[/\\]|~|[a-z]:).+", RegexOptions.IgnoreCase)]
    private static partial Regex TypicalStart();

    [GeneratedRegex(@"[/\\]")]
    private static partial Regex Separator();

    [GeneratedRegex(@"[^/\\0-9.]")]
    private static partial Regex NotPathOrNumbers();

    [GeneratedRegex(@"\s")]
    private static partial Regex Whitespace();
}

/// <summary>
/// HTML text encoding: <c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c> and, for attributes, <c>"</c>.
/// </summary>
internal static class Html
{
    public static string Encode(ReadOnlySpan<char> text)
    {
        if (text.IndexOfAny("&<>\"") < 0)
        {
            return text.ToString();
        }

        var builder = new StringBuilder(text.Length + 16);

        foreach (var c in text)
        {
            builder.Append(c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", _ => c.ToString() });
        }

        return builder.ToString();
    }
}
