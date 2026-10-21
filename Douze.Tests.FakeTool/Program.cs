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
