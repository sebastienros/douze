using Douze.Tracing;
using Xunit;

namespace Douze.Tests.Tracing;

public class TraceRecorderTests
{
    [Fact]
    public async Task NestedSpans()
    {
        var recorder = new TraceRecorder();

        using (recorder.Begin("build", "build"))
        {
            using (recorder.Begin("markdown", "step"))
            {
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }
        }

        var events = recorder.Session.Events;
        var step = Assert.Single(events, x => x.Category == "step");
        var build = Assert.Single(events, x => x.Category == "build");

        // Children end first and lie within their parent.
        Assert.Equal(["markdown", "build"], events.Select(x => x.Name));
        Assert.True(step.StartTicks >= build.StartTicks);
        Assert.True(step.StartTicks + step.DurationTicks <= build.StartTicks + build.DurationTicks);
        Assert.True(step.Duration >= TimeSpan.FromMilliseconds(4));
    }

    [Fact]
    public void UnitSpansOnlyWhenEnabled()
    {
        var withoutUnits = new TraceRecorder(traceUnits: false);
        var withUnits = new TraceRecorder(traceUnits: true);

        foreach (var recorder in new[] { withoutUnits, withUnits })
        {
            using (recorder.Begin("markdown", "step"))
            using (var unit = recorder.Begin("markdown", "unit", "/a.md"))
            {
                Assert.Equal(recorder == withUnits, unit.IsRecording);
            }
        }

        Assert.Equal(["step"], withoutUnits.Session.Events.Select(x => x.Category));
        Assert.Equal(["unit", "step"], withUnits.Session.Events.Select(x => x.Category));
        Assert.Equal("/a.md", withUnits.Session.Events[0].Unit);
        Assert.Empty(NullTraceRecorder.Instance.Session.Events);
    }

    [Fact]
    public void Args()
    {
        var recorder = new TraceRecorder();

        using (var span = recorder.Begin("og-images", "command"))
        {
            span.AddArg("command", "node og.mjs");
            span.AddArg("exit", "0");
        }

        recorder.Instant("cache miss", "io", "/a.md");

        // A default span records nothing and ignores args.
        default(TraceSpan).AddArg("ignored", "x");

        var events = recorder.Session.Events;
        Assert.Equal([new("command", "node og.mjs"), new KeyValuePair<string, string>("exit", "0")], events[0].Args);
        Assert.True(events[1].IsInstant);
        Assert.Equal("/a.md", Assert.Single(events[1].Args).Value);
        Assert.Equal(2, events.Count);
    }
}
