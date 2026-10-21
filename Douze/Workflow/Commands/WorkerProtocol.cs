using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Douze.Nodes;

namespace Douze.Workflow.Commands;

/// <summary>
/// The messages of the worker protocol (version 1): one JSON object per line, UTF-8, with a <c>type</c> field. Douze
/// writes <c>hello</c>, <c>file</c> and <c>shutdown</c> to the worker's stdin; the worker writes <c>ready</c>,
/// <c>result</c>, <c>error</c> and <c>log</c> to its stdout. Unknown types are ignored. Written with
/// <see cref="Utf8JsonWriter"/> and read with <see cref="JsonNode"/>, so no reflection is involved.
/// </summary>
internal static class WorkerProtocol
{
    public const int Version = 1;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // Text content goes as it is: only what JSON requires is escaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static ReadOnlyMemory<byte> Hello(string step, int concurrency, NodeObject options, string? site, string? source)
    {
        return Write(writer =>
        {
            writer.WriteString("type", "hello");
            writer.WriteNumber("protocol", Version);
            writer.WriteString("step", step);
            writer.WriteNumber("concurrency", concurrency);
            writer.WritePropertyName("options");
            CanonicalJson.Write(writer, options);
            writer.WriteString("site", site);
            writer.WriteString("source", source);
        });
    }

    public static ReadOnlyMemory<byte> Shutdown() => Write(writer => writer.WriteString("type", "shutdown"));

    /// <summary>
    /// A <c>file</c> request: text content as a string (<c>utf8</c>), anything else in <c>base64</c>.
    /// </summary>
    public static async ValueTask<ReadOnlyMemory<byte>> FileAsync(string id, VirtualFile file, CancellationToken ct)
    {
        string? text = null;
        ReadOnlyMemory<byte> bytes = default;

        if (file.Content.IsText)
        {
            text = await file.Content.ReadTextAsync(ct);
        }
        else
        {
            bytes = await file.Content.ReadBytesAsync(ct);
        }

        return Write(writer =>
        {
            writer.WriteString("type", "file");
            writer.WriteString("id", id);
            writer.WriteString("path", file.Path.FullName);
            writer.WriteString("contentType", file.EffectiveContentType);

            if (text != null)
            {
                writer.WriteString("encoding", "utf8");
                writer.WriteString("content", text);
            }
            else
            {
                writer.WriteString("encoding", "base64");
                writer.WriteBase64String("content", bytes.Span);
            }

            writer.WritePropertyName("metadata");
            CanonicalJson.Write(writer, file.Metadata.Own);
        });
    }

    /// <summary>
    /// Parses a line the worker wrote.
    /// </summary>
    /// <exception cref="DouzeException">The line is not a JSON object, or a known message is malformed.</exception>
    public static WorkerMessage Parse(string line)
    {
        JsonObject message;

        try
        {
            message = JsonNode.Parse(line) as JsonObject ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new DouzeException($"worker wrote a line that is not JSON: {(line.Length > 200 ? line[..200] : line)}");
        }

        switch (String(message, "type"))
        {
            case "ready":
                return new ReadyMessage(Int(message, "protocol") ?? Version, Int(message, "concurrency") ?? 1, String(message, "name"));

            case "result":
                var files = new List<WorkerFile>();

                if (message["files"] is JsonArray array)
                {
                    foreach (var item in array)
                    {
                        if (item is not JsonObject file)
                        {
                            throw new DouzeException("worker wrote a result whose files are not objects");
                        }

                        files.Add(ParseFile(file));
                    }
                }

                return new ResultMessage(RequiredString(message, "id"), files);

            case "error":
                return new ErrorMessage(
                    RequiredString(message, "id"),
                    String(message, "message") ?? "the worker reported an error",
                    String(message, "file"),
                    Int(message, "line"),
                    Int(message, "column"));

            case "log":
                return new LogMessage(String(message, "level") ?? "info", String(message, "message") ?? "");

            default:
                return UnknownMessage.Instance;
        }
    }

    private static WorkerFile ParseFile(JsonObject file)
    {
        FileContent? content = null;
        var path = String(file, "path");

        if (file["content"] is { } value)
        {
            if (value.GetValueKind() != JsonValueKind.String)
            {
                throw new DouzeException("worker wrote a file whose content is not a string");
            }

            var text = value.GetValue<string>();

            switch (String(file, "encoding") ?? "utf8")
            {
                case "utf8":
                    content = FileContent.FromText(text);
                    break;

                case "base64":
                    try
                    {
                        content = FileContent.FromBytes(Convert.FromBase64String(text), isText: false);
                    }
                    catch (FormatException)
                    {
                        throw new DouzeException("worker wrote a file whose content is not valid base64");
                    }

                    break;

                case var other:
                    throw new DouzeException($"worker wrote a file with an unknown encoding '{other}'");
            }
        }

        var metadata = file["metadata"] is { } m
            ? JsonHelper.Convert(m) as NodeObject ?? throw new DouzeException("worker wrote a file whose metadata is not an object")
            : null;

        return new WorkerFile(path, content, String(file, "contentType"), metadata);
    }

    private static ReadOnlyMemory<byte> Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(256);

        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        buffer.Write("\n"u8);
        return buffer.WrittenMemory;
    }

    private static string? String(JsonObject message, string name)
    {
        return message[name] is { } value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
    }

    private static string RequiredString(JsonObject message, string name)
    {
        return String(message, name) ?? throw new DouzeException($"worker wrote a '{String(message, "type")}' message without '{name}'");
    }

    private static int? Int(JsonObject message, string name)
    {
        return message[name] is { } value && value.GetValueKind() == JsonValueKind.Number && value.AsValue().TryGetValue<int>(out var number) ? number : null;
    }
}

internal abstract record WorkerMessage;

internal sealed record ReadyMessage(int Protocol, int Concurrency, string? Name) : WorkerMessage;

/// <summary>
/// The answer to a request: zero, one or many files.
/// </summary>
internal sealed record ResultMessage(string Id, IReadOnlyList<WorkerFile> Files) : WorkerMessage;

internal sealed record ErrorMessage(string Id, string Message, string? File, int? Line, int? Column) : WorkerMessage;

internal sealed record LogMessage(string Level, string Message) : WorkerMessage;

internal sealed record UnknownMessage : WorkerMessage
{
    public static readonly UnknownMessage Instance = new();
}

/// <summary>
/// A file of a result. Null members keep the value of the input file; <see cref="Metadata"/> is merged over its own
/// metadata (top-level keys).
/// </summary>
internal sealed record WorkerFile(string? Path, FileContent? Content, string? ContentType, NodeObject? Metadata);
