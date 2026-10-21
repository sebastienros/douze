using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Douze.Bench;

/// <summary>
/// Builds a site with a douze binary several times and prints the median wall time, user and system CPU, peak RSS and
/// number of output files as a Markdown table row. Peak RSS comes from <c>/usr/bin/time -l</c> on macOS,
/// <c>/usr/bin/time -v</c> on Linux and <see cref="Process.PeakWorkingSet64"/> on Windows. The cache mode says what each
/// build starts from: <c>clean</c>, no output folder and no cache; <c>warm</c>, the output and cache of a previous
/// build of the same site; <c>edit</c> and <c>tag</c>, the same after appending a paragraph to one page, or changing its
/// tags.
/// </summary>
internal static partial class Runner
{
    public const string Header = """
        | Label | HTML pages | Wall (s) | User (s) | Sys (s) | Peak RSS (MB) | Output files | Runs |
        |---|---:|---:|---:|---:|---:|---:|---:|
        """;

    public static async Task<int> RunAsync(string site, string outDir, string? douze, int repeat, string? label, string cache, IReadOnlyList<string> extra)
    {
        if (cache is not ("clean" or "warm" or "edit" or "tag"))
        {
            throw new BenchException($"--cache must be 'clean', 'warm', 'edit' or 'tag', not '{cache}'");
        }

        douze ??= FindDefaultBinary();

        if (!File.Exists(douze))
        {
            throw new BenchException($"douze binary '{douze}' not found; publish it first with 'dotnet publish Douze -c Release -o tool-out' or pass --douze");
        }

        var siteDir = Path.GetFullPath(site);
        var output = Path.GetFullPath(outDir);
        var cacheDir = Path.Combine(siteDir, ".douze");
        var runs = new List<Measurement>();

        if (cache != "clean")
        {
            // The build the measured ones start from.
            DeleteDirectory(output);
            DeleteDirectory(cacheDir);
            await MeasureAsync(Path.GetFullPath(douze), ["-s", siteDir, "-o", output, "-q", .. extra]);
        }

        var page = cache is "edit" or "tag" ? PickPage(siteDir) : null;

        for (var i = 0; i < repeat; i++)
        {
            switch (cache)
            {
                case "clean":
                    DeleteDirectory(output);
                    DeleteDirectory(cacheDir);
                    break;
                case "edit":
                    File.AppendAllText(page!, $"\nEdited for run {i + 1}.\n");
                    break;
                case "tag":
                    File.WriteAllText(page!, TagLine().Replace(File.ReadAllText(page!), $"tags: [edited-{i + 1}]", 1));
                    break;
            }

            var measurement = await MeasureAsync(Path.GetFullPath(douze), ["-s", siteDir, "-o", output, "-q", .. extra]);
            measurement = measurement with { Files = CountFiles(output, "*"), Pages = CountFiles(output, "*.html") };
            Console.Error.WriteLine(FormattableString.Invariant($"run {i + 1}/{repeat}: wall {measurement.Wall:F2} s, user {measurement.User:F2} s, sys {measurement.System:F2} s, peak RSS {measurement.PeakRss / 1024.0 / 1024:F0} MB, {measurement.Files} files"));
            runs.Add(measurement);
        }

        Console.WriteLine(Header);
        Console.WriteLine(FormattableString.Invariant(
            $"| {label ?? Path.GetFileName(siteDir)} | {runs[^1].Pages} | {Median(runs, x => x.Wall):F2} | {Median(runs, x => x.User):F2} | {Median(runs, x => x.System):F2} | {Median(runs, x => x.PeakRss) / 1024 / 1024:F0} | {runs[^1].Files} | {repeat} |"));
        return 0;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>
    /// The page an edit changes: the middle one of the Markdown files of the content, in path order, so that it is
    /// neither among the first pages that a nav lists nor the last.
    /// </summary>
    private static string PickPage(string site)
    {
        var pages = Directory.EnumerateFiles(site, "*.md", SearchOption.AllDirectories)
            .Where(x => !Path.GetRelativePath(site, x).Split(Path.DirectorySeparatorChar).Any(s => s.StartsWith('_') && s != "_content" || s == ".douze"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (pages.Length == 0)
        {
            throw new BenchException($"no page to edit in '{site}'");
        }

        return pages[pages.Length / 2];
    }

    [GeneratedRegex("^tags: .*$", RegexOptions.Multiline)]
    private static partial Regex TagLine();

    private static string FindDefaultBinary()
    {
        var name = OperatingSystem.IsWindows() ? "douze.exe" : "douze";

        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory != null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tool-out", name);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine("tool-out", name);
    }

    private static async Task<Measurement> MeasureAsync(string douze, string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo(douze, args) { RedirectStandardError = true, RedirectStandardOutput = true };
            var stopwatch = Stopwatch.StartNew();
            using var process = Process.Start(start)!;
            var peak = 0L;
            var stderr = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();

            // PeakWorkingSet64 is only readable while the process runs.
            while (!process.WaitForExit(50))
            {
                process.Refresh();
                peak = Math.Max(peak, process.PeakWorkingSet64);
            }

            stopwatch.Stop();
            EnsureSuccess(process.ExitCode, await stderr);
            return new Measurement(stopwatch.Elapsed.TotalSeconds, process.UserProcessorTime.TotalSeconds, process.PrivilegedProcessorTime.TotalSeconds, peak, 0, 0);
        }

        var time = new ProcessStartInfo("/usr/bin/time") { RedirectStandardError = true, RedirectStandardOutput = true };
        time.ArgumentList.Add(OperatingSystem.IsMacOS() ? "-l" : "-v");
        time.ArgumentList.Add(douze);

        foreach (var arg in args)
        {
            time.ArgumentList.Add(arg);
        }

        using var timed = Process.Start(time)!;
        var output = timed.StandardOutput.ReadToEndAsync();
        var report = await timed.StandardError.ReadToEndAsync();
        await timed.WaitForExitAsync();
        await output;
        EnsureSuccess(timed.ExitCode, report);

        return OperatingSystem.IsMacOS() ? ParseMac(report) : ParseLinux(report);
    }

    private static void EnsureSuccess(int exitCode, string stderr)
    {
        if (exitCode != 0)
        {
            throw new BenchException($"douze exited with {exitCode}:\n{stderr}");
        }
    }

    // "       12.34 real         5.67 user         0.89 sys" then "  123456789  maximum resident set size" (bytes).
    private static Measurement ParseMac(string report)
    {
        var times = MacTimes().Match(report);
        var rss = MacRss().Match(report);

        if (!times.Success || !rss.Success)
        {
            throw new BenchException($"cannot read the output of /usr/bin/time:\n{report}");
        }

        return new Measurement(Number(times.Groups[1]), Number(times.Groups[2]), Number(times.Groups[3]), long.Parse(rss.Groups[1].Value, CultureInfo.InvariantCulture), 0, 0);
    }

    private static Measurement ParseLinux(string report)
    {
        var user = LinuxField(report, "User time \\(seconds\\)");
        var system = LinuxField(report, "System time \\(seconds\\)");
        var rss = LinuxField(report, "Maximum resident set size \\(kbytes\\)");
        var wall = LinuxWall().Match(report);

        if (!wall.Success)
        {
            throw new BenchException($"cannot read the output of /usr/bin/time:\n{report}");
        }

        // h:mm:ss or m:ss.ss
        var parts = wall.Groups[1].Value.Split(':');
        var seconds = parts.Aggregate(0.0, (total, part) => (total * 60) + double.Parse(part, CultureInfo.InvariantCulture));

        return new Measurement(seconds, double.Parse(user, CultureInfo.InvariantCulture), double.Parse(system, CultureInfo.InvariantCulture), long.Parse(rss, CultureInfo.InvariantCulture) * 1024, 0, 0);
    }

    private static string LinuxField(string report, string name)
    {
        var match = Regex.Match(report, $@"{name}:\s*([0-9.]+)");
        return match.Success ? match.Groups[1].Value : throw new BenchException($"cannot read '{name}' in the output of /usr/bin/time:\n{report}");
    }

    private static double Number(Group group) => double.Parse(group.Value, CultureInfo.InvariantCulture);

    private static double Median(List<Measurement> runs, Func<Measurement, double> value)
    {
        var sorted = runs.Select(value).Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private static int CountFiles(string directory, string pattern) => Directory.Exists(directory) ? Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories).Count() : 0;

    [GeneratedRegex(@"([0-9.]+) real\s+([0-9.]+) user\s+([0-9.]+) sys")]
    private static partial Regex MacTimes();

    [GeneratedRegex(@"(\d+)\s+maximum resident set size")]
    private static partial Regex MacRss();

    [GeneratedRegex(@"Elapsed \(wall clock\) time \(h:mm:ss or m:ss\):\s*([0-9:.]+)")]
    private static partial Regex LinuxWall();

    private sealed record Measurement(double Wall, double User, double System, double PeakRss, int Files, int Pages);
}
