using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

// The fake external tool of the command step tests: 'dotnet Douze.Tests.FakeTool.dll <mode> [arguments]'.

if (args.Length == 0)
{
    return Usage();
}

var rest = args[1..];

switch (args[0])
{
    case "filter-upper": return FilterUpper();
    case "filter-count": return FilterCount(rest[0]);
    case "batch-copy": return BatchCopy(rest[0], rest[1]);
    case "batch-manifest": return BatchManifest(rest[0], rest[1]);
    case "fail": return Fail(int.Parse(rest[0]));
    case "stderr": return Stderr();
    case "hang": return Hang(rest[0]);
    case "sleep": Thread.Sleep(Timeout.Infinite); return 0;
    case "args": return Args(rest);
    case "env": return Env(rest);
    case "worker-echo": return await Worker(rest.Length > 0 ? int.Parse(rest[0]) : 1, slowShutdown: false);
    case "worker-slow": return await Worker(1, slowShutdown: true);
    default: return Usage();
}

static int Usage()
{
    Console.Error.WriteLine("usage: dotnet Douze.Tests.FakeTool.dll <mode> [arguments]");
    return 2;
}

// stdin, upper cased, to stdout.
static int FilterUpper()
{
    var text = ReadStdin();
    WriteStdout(text.ToUpperInvariant());
    return 0;
}

// Like filter-upper, and records in <folder> how many processes run at the same time: each one creates a marker,
// waits, counts the markers, and appends the count to <folder>/counts.
static int FilterCount(string folder)
{
    var text = ReadStdin();
    var marker = Path.Combine(folder, $"{Environment.ProcessId}.running");
    File.WriteAllText(marker, "");
    Thread.Sleep(300);
    var count = Directory.GetFiles(folder, "*.running").Length;
    File.AppendAllText(Path.Combine(folder, $"{Environment.ProcessId}.count"), count.ToString());
    File.Delete(marker);
    WriteStdout(text.ToUpperInvariant());
    return 0;
}

// Copies every input file to the outputs folder, upper cased, and writes the process id to /pid.txt.
static int BatchCopy(string inputs, string outputs)
{
    foreach (var file in Directory.EnumerateFiles(inputs, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(inputs, file);

        if (relative == "douze-inputs.json")
        {
            continue;
        }

        var target = Path.Combine(outputs, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, File.ReadAllText(file).ToUpperInvariant());
    }

    File.WriteAllText(Path.Combine(outputs, "pid.txt"), Environment.ProcessId.ToString());
    return 0;
}

// Reads the inputs manifest and writes /titles.txt (the title of every input, in manifest order) with a manifest
// giving it metadata and a content type.
static int BatchManifest(string inputs, string outputs)
{
    var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(inputs, "douze-inputs.json")))!;
    var files = manifest["files"]!.AsArray();
    var titles = files.Select(x => $"{x!["path"]}:{x["contentType"]}:{x["metadata"]?["title"]}");

    File.WriteAllText(Path.Combine(outputs, "titles.txt"), string.Join("\n", titles));
    File.WriteAllText(Path.Combine(outputs, "douze-outputs.json"), new JsonObject
    {
        ["files"] = new JsonArray(new JsonObject
        {
            ["path"] = "/titles.txt",
            ["contentType"] = "text/x-titles",
            ["metadata"] = new JsonObject { ["count"] = files.Count, ["step"] = manifest["step"]!.GetValue<string>() },
        }),
    }.ToJsonString());

    return 0;
}

static int Fail(int code)
{
    Console.Error.WriteLine("first line");
    Console.Error.WriteLine("it failed");
    return code;
}

// stdin to stdout, and a warning on stderr.
static int Stderr()
{
    WriteStdout(ReadStdin());
    Console.Error.WriteLine("warning: something to say");
    return 0;
}

// Starts a child process, writes both process ids to <file>, and waits forever.
static int Hang(string file)
{
    var child = Process.Start(new ProcessStartInfo("dotnet", [typeof(Program).Assembly.Location, "sleep"])
    {
        UseShellExecute = false,
    })!;

    File.WriteAllText(file + ".tmp", $"{Environment.ProcessId} {child.Id}");
    File.Move(file + ".tmp", file);
    Thread.Sleep(Timeout.Infinite);
    return 0;
}

static int Args(string[] values)
{
    WriteStdout(string.Join("\n", values));
    return 0;
}

static int Env(string[] names)
{
    WriteStdout(string.Join("\n", names.Select(x => $"{x}={Environment.GetEnvironmentVariable(x)}")));
    return 0;
}

// A worker (protocol 1) that announces <concurrency>, handles requests concurrently after the delay of their 'delay'
// metadata, and upper cases text unless the content asks for something else (see Handle). It reads stdin to its end,
// so it exits once Douze closes stdin after 'shutdown'; worker-slow never exits after 'shutdown'.
static async Task<int> Worker(int concurrency, bool slowShutdown)
{
    var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
    var stdout = Console.OpenStandardOutput();
    var writeLock = new Lock();
    var tasks = new List<Task>();
    var inflight = 0;
    var maxInflight = 0;
    var sequence = 0;
    string? hello = null;

    while (await stdin.ReadLineAsync() is { } line)
    {
        var message = JsonNode.Parse(line)!.AsObject();

        switch ((string?)message["type"])
        {
            case "hello":
                hello = line;
                Send(new JsonObject { ["type"] = "ready", ["protocol"] = 1, ["concurrency"] = concurrency, ["name"] = "fake" });
                break;

            case "file":
                tasks.Add(Task.Run(() => HandleAsync(message)));
                break;

            case "shutdown" when slowShutdown:
                Thread.Sleep(Timeout.Infinite);
                break;
        }
    }

    await Task.WhenAll(tasks);
    return 0;

    void Send(JsonNode message) => Write(message.ToJsonString() + "\n");

    void Write(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line);

        lock (writeLock)
        {
            stdout.Write(bytes);
            stdout.Flush();
        }
    }

    async Task HandleAsync(JsonObject request)
    {
        var current = Interlocked.Increment(ref inflight);

        lock (writeLock)
        {
            maxInflight = Math.Max(maxInflight, current);
        }

        try
        {
            var id = (string)request["id"]!;
            var path = (string)request["path"]!;
            var content = (string)request["content"]!;
            var delay = request["metadata"]?["delay"]?.GetValue<int>() ?? 0;

            await Task.Delay(delay);

            JsonObject File(object? text, string? filePath = null, JsonObject? metadata = null) => new()
            {
                ["path"] = filePath,
                ["encoding"] = "utf8",
                ["content"] = text?.ToString(),
                ["metadata"] = metadata,
            };

            JsonObject Stats() => new() { ["sequence"] = Interlocked.Increment(ref sequence), ["maxInflight"] = Volatile.Read(ref maxInflight) };

            JsonArray files;

            if ((string?)request["encoding"] == "base64")
            {
                var bytes = Convert.FromBase64String(content).Select(x => (byte)(255 - x)).ToArray();
                files = [new JsonObject { ["encoding"] = "base64", ["content"] = Convert.ToBase64String(bytes) }];
            }
            else
            {
                switch (content.Trim())
                {
                    case "HELLO":
                        files = [File(hello)];
                        break;
                    case "ERROR":
                        Send(new JsonObject { ["type"] = "error", ["id"] = id, ["message"] = "TS2304: Cannot find name 'foo'", ["file"] = path, ["line"] = 12, ["column"] = 3 });
                        return;
                    case "LOG":
                        Send(new JsonObject { ["type"] = "log", ["level"] = "info", ["message"] = $"log from {path}" });
                        files = [File("LOGGED")];
                        break;
                    case "EXIT":
                        Console.Error.WriteLine("crashing");
                        Environment.Exit(3);
                        return;
                    case "GARBAGE":
                        Write("this is not json\n");
                        return;
                    case "STDERR":
                        Console.Error.WriteLine("worker warning");
                        files = [File("STDERR")];
                        break;
                    case "MANY":
                        files = [File("one"), File("two", path + ".copy")];
                        break;
                    case "DROP":
                        files = [];
                        break;
                    case "RENAME":
                        files = [File("<p>renamed</p>", Path.ChangeExtension(path, ".html"))];
                        break;
                    case "META":
                        files = [new JsonObject { ["metadata"] = new JsonObject { ["extra"] = 1, ["title"] = "replaced" } }];
                        break;
                    case "HANG":
                        await Task.Delay(Timeout.Infinite);
                        return;
                    default:
                        files = [File(content.ToUpperInvariant(), metadata: Stats())];
                        break;
                }
            }

            Send(new JsonObject { ["type"] = "result", ["id"] = id, ["files"] = files });
        }
        finally
        {
            Interlocked.Decrement(ref inflight);
        }
    }
}

static string ReadStdin()
{
    using var stdin = Console.OpenStandardInput();
    using var reader = new StreamReader(stdin, new UTF8Encoding(false));
    return reader.ReadToEnd();
}

static void WriteStdout(string text)
{
    using var stdout = Console.OpenStandardOutput();
    var bytes = new UTF8Encoding(false).GetBytes(text);
    stdout.Write(bytes);
}
