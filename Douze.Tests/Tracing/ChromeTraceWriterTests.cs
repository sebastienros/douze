using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Douze.Tracing;
using Xunit;

namespace Douze.Tests.Tracing;

public class ChromeTraceWriterTests
{
    private static long Ms(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1000);

    private static TraceSession Session(params TraceEvent[] events)
    {
        var session = new TraceSession();

        foreach (var e in events)
        {
            session.Add(e);
        }

        return session;
    }

    private static TraceEvent Span(string name, string category, double start, double duration, string? unit = null, params KeyValuePair<string, string>[] args)
    {
        return new TraceEvent(name, category, unit, Ms(start), Ms(duration), 1, args);
    }

    private static ChromeTrace Write(TraceSession session, params KeyValuePair<string, string>[] otherData)
    {
        using var stream = new MemoryStream();
        ChromeTraceWriter.Write(session, stream, otherData);
        return JsonSerializer.Deserialize(stream.ToArray(), ChromeTraceContext.Default.ChromeTrace)!;
    }

    [Fact]
    public void ValidJsonObjectFormat()
    {
        var trace = Write(Session(
            Span("build", "build", 0, 100),
            Span("markdown", "step", 10, 40, args: new KeyValuePair<string, string>("files", "312"))),
            new("douze", "1.0.0"), new("site", "/src"));

        Assert.Equal("ms", trace.DisplayTimeUnit);
        Assert.Equal("1.0.0", trace.OtherData["douze"]);
        Assert.Equal("/src", trace.OtherData["site"]);

        var spans = trace.TraceEvents.Where(x => x.Ph == "X").ToArray();
        Assert.Equal(["build", "markdown"], spans.Select(x => x.Name));
        Assert.All(spans, x => Assert.Equal(1, x.Pid));
        Assert.Equal(0, spans[0].Tid);
        Assert.Equal(1, spans[1].Tid);

        // Lanes are named for Perfetto and chrome://tracing.
        Assert.Contains(trace.TraceEvents, x => x is { Ph: "M", Name: "thread_name", Tid: 1 } && x.Args!["name"].GetString() == "steps");
    }

    [Fact]
    public void OverlappingSpansGetDistinctLanes()
    {
        var trace = Write(Session(
            Span("build", "build", 0, 100),
            Span("a", "step", 0, 50),
            Span("b", "step", 10, 20),
            Span("c", "step", 30, 30),   // starts after b ended: reuses its lane
            Span("d", "step", 60, 10),   // starts after a ended: lane 1 again
            Span("a", "unit", 1, 5, "/1.md"),
            Span("a", "unit", 2, 5, "/2.md")));

        var spans = trace.TraceEvents.Where(x => x.Ph == "X").ToArray();
        var lane = spans.Where(x => x.Cat == "step").ToDictionary(x => x.Name, x => x.Tid);

        Assert.Equal(1, lane["a"]);
        Assert.Equal(2, lane["b"]);
        Assert.Equal(2, lane["c"]);
        Assert.Equal(1, lane["d"]);

        // Unit lanes come after the step lanes.
        Assert.Equal([3, 4], spans.Where(x => x.Cat == "unit").Select(x => x.Tid).Order());

        foreach (var group in spans.GroupBy(x => x.Tid))
        {
            var ordered = group.OrderBy(x => x.Ts).ToArray();

            for (var i = 1; i < ordered.Length; i++)
            {
                Assert.True(ordered[i].Ts >= ordered[i - 1].Ts + ordered[i - 1].Dur!.Value, $"overlap in lane {group.Key}");
            }
        }
    }

    [Fact]
    public void TimesAreMicrosecondsSinceTheFirstEvent()
    {
        var session = Session(
            Span("build", "build", 5, 2143),
            Span("markdown", "step", 17, 0.5));

        var spans = Write(session).TraceEvents.Where(x => x.Ph == "X").ToArray();

        Assert.Equal(0, spans[0].Ts);
        Assert.Equal(2_143_000, spans[0].Dur!.Value, 0.01);
        Assert.Equal(12_000, spans[1].Ts, 0.01);
        Assert.Equal(500, spans[1].Dur!.Value, 0.01);
    }

    [Fact]
    public void Args()
    {
        var trace = Write(Session(
            Span("og-images", "command", 0, 10, args: new KeyValuePair<string, string>("command", "node scripts/og.mjs \"<x>\"")),
            Span("markdown", "unit", 1, 2, "/a.md")));

        var command = Assert.Single(trace.TraceEvents, x => x.Cat == "command");
        Assert.Equal("node scripts/og.mjs \"<x>\"", command.Args!["command"].GetString());

        // A unit span is named after its file, and its step goes to the args.
        var unit = Assert.Single(trace.TraceEvents, x => x.Cat == "unit");
        Assert.Equal("/a.md", unit.Name);
        Assert.Equal("markdown", unit.Args!["step"].GetString());
    }
}

internal sealed record ChromeTrace(IReadOnlyList<ChromeTraceEvent> TraceEvents, string DisplayTimeUnit, Dictionary<string, string> OtherData);

internal sealed record ChromeTraceEvent(string Name, string? Cat, string Ph, double Ts, double? Dur, int Pid, int Tid, Dictionary<string, JsonElement>? Args);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ChromeTrace))]
internal sealed partial class ChromeTraceContext : JsonSerializerContext
{
}
