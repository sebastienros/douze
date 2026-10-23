using System.Diagnostics;
using System.Globalization;
using Douze.ImageBench;

// Compares NetVips (libvips, what Douze uses) and SkiaSharp on Douze's image work, and the buffer path of Douze (the
// whole source and every encoded variant in memory) with a streaming path (source file to output file through small
// buffers, hashed while written). bench/README.md, "Images: NetVips or SkiaSharp, buffers or streams".
//
//   variants --lib vips|skia --io buffer|stream --inputs <dir|file> --out <dir> [--parallel 4] [--widths 640,1280]
//            [--formats webp,jpeg] [--quality 82] [--memo] [--repeat 1]
//   svg      --lib vips|skia --svg <file> --out <dir> [--count 100] [--parallel 4]
//   compare  <dir a> <dir b>
//
// Every run prints one line: wall time, managed allocations, peak native malloc (polled), libvips' own high water
// mark, the process' peak physical footprint, and the outputs. Run each in its own process (bench/image-bench.sh).

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: Douze.ImageBench variants|svg|compare ...");
    return 2;
}

var options = Options.Parse(args[1..]);

switch (args[0])
{
    case "variants":
        return RunVariants(options);
    case "svg":
        return RunSvg(options);
    case "compare":
        return Compare.Run(options.Positional[0], options.Positional[1]);
    default:
        Console.Error.WriteLine($"unknown command '{args[0]}'");
        return 2;
}

static int RunVariants(Options options)
{
    var pipeline = Pipelines.Create(options.Get("lib", "vips"));
    var stream = options.Get("io", "buffer") == "stream";
    var inputs = Options.Inputs(options.Get("inputs"));
    var root = Directory.Exists(options.Get("inputs")) ? options.Get("inputs") : Path.GetDirectoryName(options.Get("inputs"))!;
    var output = options.Get("out");
    var parallel = int.Parse(options.Get("parallel", "4"), CultureInfo.InvariantCulture);
    var widths = options.Get("widths", "640,1280").Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
    var formats = options.Get("formats", "webp").Split(',');
    var quality = int.Parse(options.Get("quality", "82"), CultureInfo.InvariantCulture);
    var memo = options.Has("memo");
    var repeat = int.Parse(options.Get("repeat", "1"), CultureInfo.InvariantCulture);

    // What Douze's ImageProcessor._variants keeps for the whole build: every encoded variant.
    var memoized = new System.Collections.Concurrent.ConcurrentBag<byte[]>();
    long outputs = 0, outputBytes = 0;

    // A first pass over one source, not measured: JIT, library initialization.
    Warmup(pipeline, inputs[0], formats, quality, stream, Path.Combine(output, ".warmup"));

    using var meter = Meter.Start();

    for (var r = 0; r < repeat; r++)
    {
        Parallel.ForEach(inputs, new ParallelOptions { MaxDegreeOfParallelism = parallel }, input =>
        {
            var relative = Path.GetRelativePath(root, input);
            var folder = Path.Combine(output, Path.GetDirectoryName(relative) ?? "");
            Directory.CreateDirectory(folder);
            var name = Path.GetFileNameWithoutExtension(relative);

            // The buffer path reads the source once for all its variants, as Douze does.
            var source = stream ? null : File.ReadAllBytes(input);

            foreach (var format in formats)
            {
                foreach (var width in widths)
                {
                    var variant = new Variant(width, format, quality);
                    Encoded result;

                    if (stream)
                    {
                        result = pipeline.EncodeStream(input, variant, folder, name);
                    }
                    else
                    {
                        var (bytes, w, h) = pipeline.EncodeBuffer(source!, variant);
                        var path = Path.Combine(folder, Naming.FileName(name, w, Naming.Hash(bytes), format));
                        File.WriteAllBytes(path, bytes);
                        result = new Encoded(path, w, h, bytes.Length);

                        if (memo)
                        {
                            memoized.Add(bytes);
                        }
                    }

                    Interlocked.Increment(ref outputs);
                    Interlocked.Add(ref outputBytes, result.Length);
                }
            }
        });
    }

    var report = meter.Stop();
    GC.KeepAlive(memoized);
    Console.WriteLine(report.Format($"variants {pipeline.Name} {(stream ? "stream" : "buffer")}{(memo ? "+memo" : "")} -p {parallel}: {inputs.Length} sources, {outputs} outputs, {outputBytes / 1024.0 / 1024:F1} MB"));
    return 0;
}

static void Warmup(IPipeline pipeline, string input, string[] formats, int quality, bool stream, string folder)
{
    Directory.CreateDirectory(folder);

    foreach (var format in formats)
    {
        if (stream)
        {
            pipeline.EncodeStream(input, new Variant(640, format, quality), folder, "warmup");
        }
        else
        {
            pipeline.EncodeBuffer(File.ReadAllBytes(input), new Variant(640, format, quality));
        }
    }

    Directory.Delete(folder, recursive: true);
}

static int RunSvg(Options options)
{
    var pipeline = Pipelines.Create(options.Get("lib", "vips"));
    var svg = File.ReadAllText(options.Get("svg"));
    var output = options.Get("out");
    var count = int.Parse(options.Get("count", "100"), CultureInfo.InvariantCulture);
    var parallel = int.Parse(options.Get("parallel", "4"), CultureInfo.InvariantCulture);
    Directory.CreateDirectory(output);
    long outputBytes = 0;

    pipeline.RasterizeSvg(svg, 1200, 630);

    using var meter = Meter.Start();

    // A different SVG each time, as the social images of pages differ (no memoized raster).
    Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = parallel }, i =>
    {
        var png = pipeline.RasterizeSvg(svg.Replace("{{n}}", i.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal), 1200, 630);
        File.WriteAllBytes(Path.Combine(output, $"card-{i}.png"), png);
        Interlocked.Add(ref outputBytes, png.Length);
    });

    var report = meter.Stop();
    Console.WriteLine(report.Format($"svg {pipeline.Name} -p {parallel}: {count} PNGs, {outputBytes / 1024.0 / 1024:F1} MB"));
    return 0;
}

internal sealed class Options
{
    private readonly Dictionary<string, string> _named = new(StringComparer.Ordinal);

    public List<string> Positional { get; } = [];

    public static Options Parse(string[] args)
    {
        var options = new Options();

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                var key = args[i][2..];
                options._named[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            }
            else
            {
                options.Positional.Add(args[i]);
            }
        }

        return options;
    }

    public bool Has(string key) => _named.ContainsKey(key);

    public string Get(string key, string? defaultValue = null) => _named.TryGetValue(key, out var value) ? value : defaultValue ?? throw new ArgumentException($"--{key} is required");

    /// <summary>
    /// The PNG and JPEG files of a folder (recursively, sorted), or one file.
    /// </summary>
    public static string[] Inputs(string path)
    {
        if (File.Exists(path))
        {
            return [path];
        }

        return [.. Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(x => Path.GetExtension(x).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg")
            .Order(StringComparer.Ordinal)];
    }
}

internal readonly record struct Variant(int Width, string Format, int Quality);

internal sealed record Encoded(string Path, int Width, int Height, long Length);

internal static class Naming
{
    /// <summary>
    /// Douze's variant names: <c>&lt;name&gt;-&lt;width&gt;.&lt;hash8&gt;.&lt;ext&gt;</c>, XxHash128 of the bytes.
    /// </summary>
    public static string FileName(string name, int width, UInt128 hash, string format)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{name}-{width}.{Hash8(hash)}.{(format == "jpeg" ? "jpg" : format)}");
    }

    public static UInt128 Hash(ReadOnlySpan<byte> bytes) => System.IO.Hashing.XxHash128.HashToUInt128(bytes);

    private static string Hash8(UInt128 hash) => hash.ToString("x32", CultureInfo.InvariantCulture)[..8];
}

/// <summary>
/// A file written through a stream and hashed on the way, under a temporary name, then renamed to its hashed name:
/// what an encoder writing to a stream needs, since the name depends on the content.
/// </summary>
internal sealed class HashingFileStream : Stream
{
    private readonly System.IO.Hashing.XxHash128 _hash = new();
    private readonly FileStream _file;
    private readonly string _temp;
    private long _length;

    public HashingFileStream(string folder)
    {
        _temp = Path.Combine(folder, $".tmp-{Guid.NewGuid():N}");

        // A small buffer: the encoder writes chunks, the file system gets them in 16 KB writes.
        _file = new FileStream(_temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 16 * 1024);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _length;
    public override long Position { get => _length; set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _hash.Append(buffer);
        _file.Write(buffer);
        _length += buffer.Length;
    }

    /// <summary>
    /// Closes the file and renames it to the name made of its hash; returns the path and length.
    /// </summary>
    public (string Path, long Length) Complete(Func<UInt128, string> name)
    {
        _file.Dispose();
        var path = Path.Combine(Path.GetDirectoryName(_temp)!, name(_hash.GetCurrentHashAsUInt128()));
        File.Move(_temp, path, overwrite: true);
        return (path, _length);
    }

    public override void Flush() => _file.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _file.Dispose();

            if (File.Exists(_temp))
            {
                File.Delete(_temp);
            }
        }

        base.Dispose(disposing);
    }
}
