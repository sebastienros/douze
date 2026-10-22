using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Douze.CodeHover;

/// <summary>
/// What a code step gets from a language server, whatever the language: hovers, diagnostics and semantic tokens, sent
/// over an <see cref="LspConnection"/> with at most <see cref="MaxInFlight"/> requests in flight, so that a step can send
/// the requests of all its snippets at once and let the server answer them in parallel.
/// </summary>
internal sealed class LspClient
{
    /// <summary>
    /// The requests sent at once, at most.
    /// </summary>
    public const int MaxInFlight = 512;

    // The token types of the protocol, which servers that filter their legend by the client's (gopls) need.
    private static readonly string[] TokenTypes =
    [
        "namespace", "type", "class", "enum", "interface", "struct", "typeParameter", "parameter", "variable", "property",
        "enumMember", "event", "function", "method", "macro", "keyword", "modifier", "comment", "string", "number",
        "regexp", "operator", "decorator", "label",
    ];

    private readonly SemaphoreSlim _inFlight = new(MaxInFlight, MaxInFlight);
    private string[] _tokenLegend = [];

    private LspClient(LspConnection connection)
    {
        Connection = connection;
    }

    public LspConnection Connection { get; }

    /// <exception cref="DouzeException">The process cannot start.</exception>
    public static LspClient Start(ProcessStartInfo info, string name) => new(LspConnection.Start(info, name));

    /// <summary>
    /// Sends <c>initialize</c> for the workspace <paramref name="folder"/>, with the capabilities of hovers in Markdown,
    /// pulled diagnostics, completions and semantic tokens, then <c>initialized</c>. Returns the server's capabilities.
    /// </summary>
    public async Task<JsonElement> InitializeAsync(string folder, string name, Action<Utf8JsonWriter>? writeInitializationOptions, CancellationToken ct)
    {
        var uri = new Uri(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');

        var result = await Connection.RequestAsync("initialize", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("processId", Environment.ProcessId);
            writer.WriteString("rootUri", uri);
            writer.WriteStartArray("workspaceFolders");
            writer.WriteStartObject();
            writer.WriteString("uri", uri);
            writer.WriteString("name", name);
            writer.WriteEndObject();
            writer.WriteEndArray();

            if (writeInitializationOptions != null)
            {
                writer.WritePropertyName("initializationOptions");
                writeInitializationOptions(writer);
            }

            writer.WriteStartObject("capabilities");
            writer.WriteStartObject("general");
            writer.WriteStartArray("positionEncodings");
            writer.WriteStringValue("utf-16");
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteStartObject("textDocument");
            writer.WriteStartObject("hover");
            writer.WriteStartArray("contentFormat");
            writer.WriteStringValue("markdown");
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteStartObject("diagnostic");
            writer.WriteEndObject();
            writer.WriteStartObject("completion");
            writer.WriteEndObject();
            writer.WriteStartObject("semanticTokens");
            writer.WriteStartObject("requests");
            writer.WriteBoolean("full", true);
            writer.WriteEndObject();
            writer.WriteStartArray("tokenTypes");

            foreach (var type in TokenTypes)
            {
                writer.WriteStringValue(type);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("tokenModifiers");
            writer.WriteEndArray();
            writer.WriteStartArray("formats");
            writer.WriteStringValue("relative");
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }, ct);

        await Connection.NotifyAsync("initialized", static w =>
        {
            w.WriteStartObject();
            w.WriteEndObject();
        });

        var capabilities = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("capabilities", out var c) ? c : default;

        if (capabilities.ValueKind == JsonValueKind.Object
            && capabilities.TryGetProperty("semanticTokensProvider", out var provider)
            && provider.ValueKind == JsonValueKind.Object
            && provider.TryGetProperty("legend", out var legend))
        {
            _tokenLegend = [.. legend.GetProperty("tokenTypes").EnumerateArray().Select(x => x.GetString() ?? "")];
        }

        return capabilities;
    }

    /// <summary>
    /// Sends a request, waiting first while <see cref="MaxInFlight"/> requests are in flight.
    /// </summary>
    public async Task<JsonElement> RequestAsync(string method, Action<Utf8JsonWriter> writeParams, CancellationToken ct)
    {
        await _inFlight.WaitAsync(ct);

        try
        {
            return await Connection.RequestAsync(method, writeParams, ct);
        }
        finally
        {
            _inFlight.Release();
        }
    }

    public Task OpenAsync(string uri, string languageId, string text) => Connection.NotifyAsync("textDocument/didOpen", writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartObject("textDocument");
        writer.WriteString("uri", uri);
        writer.WriteString("languageId", languageId);
        writer.WriteNumber("version", 1);
        writer.WriteString("text", text);
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    public Task CloseAsync(string uri) => Connection.NotifyAsync("textDocument/didClose", writer => WriteDocument(writer, uri));

    /// <summary>
    /// The Markdown of the hover at a position, or null when there is none, or when it is about something around the
    /// position rather than at it (its range does not hold the position: the constructor of <c>new X</c>).
    /// </summary>
    public async Task<string?> HoverAsync(string uri, int line, int character, CancellationToken ct)
    {
        var result = await RequestAsync("textDocument/hover", writer => WritePosition(writer, uri, line, character, null), ct);

        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("contents", out var contents)
            || !contents.TryGetProperty("value", out var value))
        {
            return null;
        }

        if (result.TryGetProperty("range", out var range))
        {
            var start = ReadPosition(range.GetProperty("start"));
            var end = ReadPosition(range.GetProperty("end"));

            if ((line, character).CompareTo(start) < 0 || (line, character).CompareTo(end) > 0)
            {
                return null;
            }
        }

        return value.GetString();
    }

    /// <summary>
    /// The diagnostics of a document (<c>textDocument/diagnostic</c>).
    /// </summary>
    public async Task<List<LspDiagnostic>> DiagnosticsAsync(string uri, CancellationToken ct)
    {
        var result = await RequestAsync("textDocument/diagnostic", writer => WriteDocument(writer, uri), ct);
        var diagnostics = new List<LspDiagnostic>();

        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("items", out var items))
        {
            return diagnostics;
        }

        foreach (var item in items.EnumerateArray())
        {
            var range = item.GetProperty("range");
            var code = item.TryGetProperty("code", out var c) ? c.ValueKind == JsonValueKind.Number ? c.GetInt64().ToString(CultureInfo.InvariantCulture) : c.GetString() ?? "" : "";

            diagnostics.Add(new LspDiagnostic(
                ReadPosition(range.GetProperty("start")),
                ReadPosition(range.GetProperty("end")),
                item.TryGetProperty("severity", out var s) ? s.GetInt32() : 1,
                code,
                item.GetProperty("message").GetString() ?? ""));
        }

        return diagnostics;
    }

    /// <summary>
    /// The semantic tokens of a document (<c>textDocument/semanticTokens/full</c>), with their type from the server's
    /// legend.
    /// </summary>
    public async Task<List<SemanticToken>> SemanticTokensAsync(string uri, CancellationToken ct)
    {
        var result = await RequestAsync("textDocument/semanticTokens/full", writer => WriteDocument(writer, uri), ct);
        var tokens = new List<SemanticToken>();

        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("data", out var data))
        {
            return tokens;
        }

        // Five integers a token: line delta, start delta (from the previous token when on its line), length, type and
        // modifiers.
        var values = data.EnumerateArray().Select(x => x.GetInt32()).ToArray();
        var line = 0;
        var character = 0;

        for (var i = 0; i + 4 < values.Length; i += 5)
        {
            if (values[i] != 0)
            {
                line += values[i];
                character = values[i + 1];
            }
            else
            {
                character += values[i + 1];
            }

            var type = values[i + 3] < _tokenLegend.Length ? _tokenLegend[values[i + 3]] : "";
            tokens.Add(new SemanticToken(line, character, values[i + 2], type));
        }

        return tokens;
    }

    /// <summary>
    /// Sends <c>shutdown</c> and <c>exit</c>, then kills the process if it is still running after
    /// <paramref name="grace"/>. Never throws.
    /// </summary>
    public Task ShutdownAsync(TimeSpan grace) => Connection.CloseAsync(grace);

    public void Kill() => Connection.Kill();

    public static void WriteDocument(Utf8JsonWriter writer, string uri)
    {
        writer.WriteStartObject();
        writer.WriteStartObject("textDocument");
        writer.WriteString("uri", uri);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    public static void WritePosition(Utf8JsonWriter writer, string uri, int line, int character, string? trigger)
    {
        writer.WriteStartObject();
        writer.WriteStartObject("textDocument");
        writer.WriteString("uri", uri);
        writer.WriteEndObject();
        writer.WriteStartObject("position");
        writer.WriteNumber("line", line);
        writer.WriteNumber("character", character);
        writer.WriteEndObject();

        if (trigger != null)
        {
            writer.WriteStartObject("context");
            writer.WriteNumber("triggerKind", 2);
            writer.WriteString("triggerCharacter", trigger);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    public static (int Line, int Character) ReadPosition(JsonElement position) =>
        (position.GetProperty("line").GetInt32(), position.GetProperty("character").GetInt32());
}

/// <summary>
/// A diagnostic of a language server: its range, severity (1 error, 2 warning, 3 information, 4 hint), code (as a
/// string, whatever its type in the protocol) and message.
/// </summary>
internal sealed record LspDiagnostic((int Line, int Character) Start, (int Line, int Character) End, int Severity, string Code, string Message);

/// <summary>
/// A semantic token: where it is and its type (<c>variable</c>, <c>method</c>, <c>keyword</c>...).
/// </summary>
internal readonly record struct SemanticToken(int Line, int Character, int Length, string Type)
{
    // The token types of symbols, which have a hover: the protocol's, and those of Roslyn and gopls. Keywords,
    // literals, comments, operators and punctuation have none worth showing.
    private static readonly HashSet<string> Symbols = new(StringComparer.Ordinal)
    {
        "namespace", "type", "class", "enum", "interface", "struct", "typeParameter", "parameter", "variable", "property",
        "enumMember", "event", "function", "method", "label", "delegate", "extensionMethod", "field", "constant",
        "recordClass", "recordStruct", "module",
    };

    /// <summary>
    /// Whether a token of this type names a symbol.
    /// </summary>
    public static bool IsSymbol(string type) => Symbols.Contains(type);
}

/// <summary>
/// What checking a snippet gave: a result, why the snippet is rejected, or both (a result with errors, which a step
/// that only warns renders).
/// </summary>
internal sealed record CodeOutcome(TwoslashResult? Result, string? Error);

/// <summary>
/// A file every snippet sees next to its own files, at <see cref="Path"/> relative to the snippet's folder or project
/// (such as a declaration file that the snippets import).
/// </summary>
internal sealed record SharedFile(string Path, byte[] Content);
