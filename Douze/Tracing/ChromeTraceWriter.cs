using System.Diagnostics;
using System.Text.Json;

namespace Douze.Tracing;

/// <summary>
/// Writes a <see cref="TraceSession"/> as Chrome trace events (JSON Object Format), which Perfetto, <c>chrome://tracing</c>
/// and <c>timeline.html</c> read.
/// </summary>
/// <remarks>
/// Times are microseconds since the first event. Each span gets a lane (<c>tid</c>): the build is lane 0, step spans
/// share the lanes after it and every other span (units, commands, I/O) the lanes after those. Within a group, a span
/// takes the lowest lane whose last span ended before it starts, so overlapping spans never share a lane.
/// </remarks>
internal static class ChromeTraceWriter
{
    private const string BuildCategory = "build";
    private const string StepCategory = "step";

    public static void Write(TraceSession session, Stream stream, IEnumerable<KeyValuePair<string, string>>? otherData = null)
    {
        // The default encoder escapes '<' and '>', so the JSON is safe inside a <script> element.
        using var writer = new Utf8JsonWriter(stream);
        Write(session, writer, otherData);
    }

    private static void Write(TraceSession session, Utf8JsonWriter writer, IEnumerable<KeyValuePair<string, string>>? otherData)
    {
        var events = session.Events
            .OrderBy(x => x.StartTicks)
            .ThenBy(x => Group(x))
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ThenBy(x => x.Unit, StringComparer.Ordinal)
            .ToArray();

        var origin = events.Length == 0 ? 0 : events[0].StartTicks;
        var lanes = AssignLanes(events, out var stepLanes, out var otherLanes);

        writer.WriteStartObject();
        writer.WriteStartArray("traceEvents");

        WriteMetadata(writer, "process_name", 0, "douze");
        WriteLaneName(writer, 0, "build");

        for (var i = 0; i < stepLanes; i++)
        {
            WriteLaneName(writer, 1 + i, stepLanes == 1 ? "steps" : $"steps {i + 1}");
        }

        for (var i = 0; i < otherLanes; i++)
        {
            WriteLaneName(writer, 1 + stepLanes + i, otherLanes == 1 ? "units" : $"units {i + 1}");
        }

        for (var i = 0; i < events.Length; i++)
        {
            var e = events[i];
            var isUnit = e.Category == TraceRecorder.UnitCategory && e.Unit != null;

            writer.WriteStartObject();
            writer.WriteString("name", isUnit ? e.Unit : e.Name);
            writer.WriteString("cat", e.Category);
            writer.WriteString("ph", e.IsInstant ? "i" : "X");
            writer.WriteNumber("ts", Microseconds(e.StartTicks - origin));

            if (e.IsInstant)
            {
                writer.WriteString("s", "g");
            }
            else
            {
                writer.WriteNumber("dur", Microseconds(e.DurationTicks));
            }

            writer.WriteNumber("pid", 1);
            writer.WriteNumber("tid", lanes[i]);

            if (e.Args.Count > 0 || e.Unit != null)
            {
                writer.WriteStartObject("args");

                if (e.Unit != null)
                {
                    // A unit span is named after its file or group; the step goes to the args.
                    writer.WriteString(isUnit ? "step" : "unit", isUnit ? e.Name : e.Unit);
                }

                foreach (var (key, value) in e.Args)
                {
                    writer.WriteString(key, value);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteString("displayTimeUnit", "ms");

        writer.WriteStartObject("otherData");

        foreach (var (key, value) in otherData ?? [])
        {
            writer.WriteString(key, value);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    // 0: build, 1: steps, 2: everything else.
    private static int Group(TraceEvent e) => e.Category switch
    {
        BuildCategory => 0,
        StepCategory => 1,
        _ => 2,
    };

    /// <summary>
    /// The lane of each event, in the order of <paramref name="events"/> (sorted by start).
    /// </summary>
    private static int[] AssignLanes(TraceEvent[] events, out int stepLanes, out int otherLanes)
    {
        var lanes = new int[events.Length];
        var stepEnds = new List<long>();
        var otherEnds = new List<long>();

        for (var i = 0; i < events.Length; i++)
        {
            var e = events[i];

            lanes[i] = Group(e) switch
            {
                0 => 0,
                1 => 1 + Take(stepEnds, e),
                _ => -1 - Take(otherEnds, e),
            };
        }

        stepLanes = stepEnds.Count;
        otherLanes = otherEnds.Count;

        // Other lanes come after every step lane, which is known once all events are placed.
        for (var i = 0; i < lanes.Length; i++)
        {
            if (lanes[i] < 0)
            {
                lanes[i] = stepLanes - lanes[i];
            }
        }

        return lanes;

        static int Take(List<long> ends, TraceEvent e)
        {
            var end = e.StartTicks + e.DurationTicks;

            for (var lane = 0; lane < ends.Count; lane++)
            {
                if (ends[lane] <= e.StartTicks)
                {
                    ends[lane] = end;
                    return lane;
                }
            }

            ends.Add(end);
            return ends.Count - 1;
        }
    }

    private static void WriteLaneName(Utf8JsonWriter writer, int lane, string name)
    {
        WriteMetadata(writer, "thread_name", lane, name);

        writer.WriteStartObject();
        writer.WriteString("name", "thread_sort_index");
        writer.WriteString("ph", "M");
        writer.WriteNumber("pid", 1);
        writer.WriteNumber("tid", lane);
        writer.WriteStartObject("args");
        writer.WriteNumber("sort_index", lane);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteMetadata(Utf8JsonWriter writer, string name, int lane, string value)
    {
        writer.WriteStartObject();
        writer.WriteString("name", name);
        writer.WriteString("ph", "M");
        writer.WriteNumber("pid", 1);
        writer.WriteNumber("tid", lane);
        writer.WriteStartObject("args");
        writer.WriteString("name", value);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    // Rounded to the nanosecond: the precision of the clock, without the noise of the division.
    private static double Microseconds(long ticks) => Math.Round(ticks * 1_000_000.0 / Stopwatch.Frequency, 3);
}
