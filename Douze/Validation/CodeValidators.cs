using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Douze.Liquid;
using Douze.Workflow.Yaml;
using Fluid;
using SharpYaml;
using SharpYaml.Model;

namespace Douze.Validation;

/// <summary>
/// An error of a code sample, at a line and column of the sample (1 based); <paramref name="Column"/> is 0 when only the
/// line is known, <paramref name="Line"/> is 0 when the position is not known.
/// </summary>
internal sealed record CodeError(int Line, int Column, string Message)
{
    public override string ToString() => Line == 0 ? Message : Column == 0 ? $"line {Line}: {Message}" : $"line {Line}, column {Column}: {Message}";
}

/// <summary>
/// The validators of the <c>check-code</c> step: in-process parsers that tell whether a code sample is well formed, by
/// language. Each returns the errors of a sample, none when it is valid.
/// </summary>
internal static partial class CodeValidators
{
    public const string Yaml = "yaml";
    public const string Json = "json";
    public const string Jsonc = "jsonc";
    public const string Jsonl = "jsonl";
    public const string Xml = "xml";
    public const string Liquid = "liquid";

    /// <summary>
    /// The validators, in the order of the documentation.
    /// </summary>
    public static readonly ImmutableArray<string> Names = [Yaml, Json, Jsonc, Jsonl, Xml, Liquid];

    /// <summary>
    /// The fence languages and file extensions that a validator checks without an <c>aliases</c> option.
    /// </summary>
    public static readonly FrozenDictionary<string, string> BuiltInAliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["yaml"] = Yaml,
        ["yml"] = Yaml,
        ["json"] = Json,
        ["webmanifest"] = Json,
        ["jsonc"] = Jsonc,
        ["jsonl"] = Jsonl,
        ["ndjson"] = Jsonl,
        ["xml"] = Xml,
        ["svg"] = Xml,
        ["xsl"] = Xml,
        ["xslt"] = Xml,
        ["rss"] = Xml,
        ["atom"] = Xml,
        ["liquid"] = Liquid,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Every document of a YAML stream, and the keys of its mappings: a key defined twice in a mapping is an error.
    /// </summary>
    public static IReadOnlyList<CodeError> CheckYaml(string code) => CheckYaml(code, out _);

    /// <param name="code">The YAML text.</param>
    /// <param name="stream">The parsed stream, when there is no syntax error.</param>
    public static IReadOnlyList<CodeError> CheckYaml(string code, out YamlStream? stream)
    {
        try
        {
            using var reader = new StringReader(code);
            stream = YamlStream.Load(reader);
        }
        catch (YamlException e)
        {
            stream = null;
            return [new CodeError(e.Start.Line + 1, e.Start.Column + 1, YamlSource.ErrorMessage(e.Message))];
        }

        var errors = new List<CodeError>();

        foreach (var document in stream)
        {
            AddDuplicateKeys(document.Contents, errors);
        }

        return errors;
    }

    private static void AddDuplicateKeys(YamlElement? node, List<CodeError> errors)
    {
        switch (node)
        {
            case YamlMapping mapping:
                var keys = new HashSet<string>(StringComparer.Ordinal);

                foreach (var (key, value) in mapping)
                {
                    // '<<' merges a mapping: it is not a key of the data.
                    if (key is YamlValue { Value: var text } && text != "<<" && !keys.Add(text))
                    {
                        var start = YamlSource.Start(key);
                        errors.Add(new CodeError(start.Line + 1, start.Column + 1, $"duplicate key '{text}'"));
                    }

                    AddDuplicateKeys(key, errors);
                    AddDuplicateKeys(value, errors);
                }

                break;

            case YamlSequence sequence:
                foreach (var item in sequence)
                {
                    AddDuplicateKeys(item, errors);
                }

                break;
        }
    }

    /// <summary>
    /// A JSON value; with <paramref name="comments"/> (<c>jsonc</c>), comments and trailing commas are allowed. A
    /// property defined twice in an object is an error.
    /// </summary>
    public static IReadOnlyList<CodeError> CheckJson(string code, bool comments)
    {
        try
        {
            using var _ = JsonDocument.Parse(code, new JsonDocumentOptions
            {
                CommentHandling = comments ? JsonCommentHandling.Skip : JsonCommentHandling.Disallow,
                AllowTrailingCommas = comments,
                AllowDuplicateProperties = false,
            });

            return [];
        }
        catch (JsonException e)
        {
            var message = JsonPosition().Replace(e.Message, "").Trim();
            return e.LineNumber is { } line
                ? [new CodeError((int)line + 1, (int)(e.BytePositionInLine ?? 0) + 1, message)]
                : [new CodeError(0, 0, message)];
        }
    }

    /// <summary>
    /// JSON Lines: a JSON value on every line that is not blank, such as the messages of a protocol.
    /// </summary>
    public static IReadOnlyList<CodeError> CheckJsonLines(string code)
    {
        var errors = new List<CodeError>();
        var lines = code.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length > 0)
            {
                errors.AddRange(CheckJson(lines[i], comments: false).Select(x => x with { Line = i + 1, Column = x.Line > 0 ? x.Column : 0 }));
            }
        }

        return errors;
    }

    /// <summary>
    /// Well-formed XML: a document, or with <paramref name="fragment"/> any sequence of elements and text (a sample
    /// of several elements). A DTD is ignored and nothing is fetched.
    /// </summary>
    public static IReadOnlyList<CodeError> CheckXml(string code, bool fragment)
    {
        var settings = new XmlReaderSettings
        {
            ConformanceLevel = fragment ? ConformanceLevel.Fragment : ConformanceLevel.Document,
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
        };

        try
        {
            using var reader = XmlReader.Create(new StringReader(code), settings);

            while (reader.Read())
            {
            }

            return [];
        }
        catch (XmlException e)
        {
            var message = XmlPosition().Replace(e.Message, "").Trim();
            return [new CodeError(e.LineNumber, e.LinePosition, message)];
        }
    }

    /// <summary>
    /// A Liquid template, parsed with <paramref name="parser"/>: the build's, with Douze's tags and the site's
    /// shortcodes.
    /// </summary>
    public static IReadOnlyList<CodeError> CheckLiquid(string code, FluidParser parser)
    {
        try
        {
            parser.Parse(code);
            return [];
        }
        catch (ParseException e)
        {
            var reason = TemplateLoader.Reason(e);
            return e.Position is { } position ? [new CodeError(position.Line, position.Column, reason)] : [new CodeError(0, 0, reason)];
        }
    }

    // " LineNumber: 0 | BytePositionInLine: 5." at the end of System.Text.Json's messages.
    [GeneratedRegex(@"\s*(Path: \S*\s*\|\s*)?LineNumber: \d+ \| BytePositionInLine: \d+\.?")]
    private static partial Regex JsonPosition();

    // " Line 4, position 3." at the end of XmlReader's messages.
    [GeneratedRegex(@"\s*Line \d+, position \d+\.?$")]
    private static partial Regex XmlPosition();
}
