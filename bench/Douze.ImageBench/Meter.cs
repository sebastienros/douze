using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Douze.ImageBench;

/// <summary>
/// Measures a run: wall time, managed allocations (<c>GC.GetTotalAllocatedBytes</c>), the peak of the managed heap (polled) and
/// of native malloc (macOS: <c>malloc_zone_statistics</c> over every zone, polled every 5 ms, above what was in use when
/// the run started), libvips' own high water mark (<c>vips_tracked_get_mem_highwater</c>: its pixel buffers), and the
/// process' lifetime peak physical footprint (<c>proc_pid_rusage</c>, what <c>/usr/bin/time -l</c> prints as "peak
/// memory footprint").
/// </summary>
internal sealed class Meter : IDisposable
{
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private readonly long _allocated = GC.GetTotalAllocatedBytes(precise: true);
    private readonly long _mallocBase = MallocInUse();
    private readonly Thread _poller;
    private volatile bool _stop;
    private long _mallocPeak;
    private long _managedPeak;

    private Meter()
    {
        _mallocPeak = _mallocBase;
        _poller = new Thread(Poll) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _poller.Start();
    }

    public static Meter Start()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        return new Meter();
    }

    private void Poll()
    {
        while (!_stop)
        {
            _mallocPeak = Math.Max(_mallocPeak, MallocInUse());
            _managedPeak = Math.Max(_managedPeak, GC.GetTotalMemory(forceFullCollection: false));
            Thread.Sleep(5);
        }
    }

    public Report Stop()
    {
        var wall = _wall.Elapsed;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - _allocated;
        _stop = true;
        _poller.Join();

        return new Report(wall, allocated, _managedPeak, _mallocPeak - _mallocBase, VipsHighwater(), PeakFootprint());
    }

    public void Dispose() => _stop = true;

    private static long VipsHighwater()
    {
        // Only once libvips is loaded: a SkiaSharp run never loads it.
        return AppDomain.CurrentDomain.GetAssemblies().Any(x => x.GetName().Name == "NetVips") ? VipsStats() : 0;

        static long VipsStats() => (long)NetVips.Stats.MemHighwater;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MallocStatistics
    {
        public uint BlocksInUse;
        public nuint SizeInUse;
        public nuint MaxSizeInUse;
        public nuint SizeAllocated;
    }

    [DllImport("libSystem.dylib")]
    private static extern void malloc_zone_statistics(IntPtr zone, out MallocStatistics stats);

    [DllImport("libSystem.dylib")]
    private static extern unsafe int proc_pid_rusage(int pid, int flavor, ulong* buffer);

    private static long MallocInUse()
    {
        malloc_zone_statistics(IntPtr.Zero, out var stats);
        return (long)stats.SizeInUse;
    }

    private static unsafe long PeakFootprint()
    {
        // rusage_info_v4: a 16 byte uuid, then 64 bit fields; ri_lifetime_max_phys_footprint is the 29th of those.
        var buffer = stackalloc ulong[64];
        return proc_pid_rusage(Environment.ProcessId, 4, buffer) == 0 ? (long)buffer[2 + 28] : 0;
    }
}

internal sealed record Report(TimeSpan Wall, long Allocated, long ManagedPeak, long MallocPeak, long VipsPeak, long Footprint)
{
    public string Format(string what)
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"{what} | wall {Wall.TotalSeconds:F2} s | allocated {Mb(Allocated)} | managed heap peak {Mb(ManagedPeak)} | malloc peak {Mb(MallocPeak)} | vips peak {Mb(VipsPeak)} | footprint peak {Mb(Footprint)}");
    }

    private static string Mb(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024:F1} MB");
}
