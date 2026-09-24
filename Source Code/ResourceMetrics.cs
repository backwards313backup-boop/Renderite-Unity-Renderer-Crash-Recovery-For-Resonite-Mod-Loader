using System.Diagnostics;

namespace RenderiteRecovery;

internal static class ResourceMetrics
{
    internal enum Area
    {
        Recording,
        EngineHooks,
        Background,
        Recovery,
        Panel
    }

    internal static readonly Area[] Areas = Enum.GetValues<Area>();
    private const int SampleCount = 6;

    private static readonly long[] Ticks = new long[Areas.Length];
    private static readonly long[] Allocated = new long[Areas.Length];
    private static readonly Sample[] Samples = new Sample[SampleCount];
    private static readonly object SampleGate = new();
    private static int _sampleCount;
    private static int _nextSample;
    private static long _lastSampleTick;
    private static long _frames;
    [ThreadStatic] private static int _depth;

    private sealed record Sample(long Timestamp, long[] Ticks, long[] Allocated, long Frames, TimeSpan ProcessCpu);

    internal readonly struct Scope : IDisposable
    {
        private readonly int _area;
        private readonly long _start;
        private readonly long _allocatedStart;

        internal Scope(Area area)
        {
            if (_depth++ > 0)
            {
                _area = -1;
                _start = _allocatedStart = 0;
                return;
            }
            _area = (int)area;
            _start = Stopwatch.GetTimestamp();
            _allocatedStart = GC.GetAllocatedBytesForCurrentThread();
        }

        public void Dispose()
        {
            _depth--;
            if (_area < 0)
                return;

            Interlocked.Add(ref Ticks[_area], Stopwatch.GetTimestamp() - _start);
            Interlocked.Add(ref Allocated[_area], GC.GetAllocatedBytesForCurrentThread() - _allocatedStart);
        }
    }

    internal static Scope Measure(Area area) => new(area);

    internal static void CountFrame() => Interlocked.Increment(ref _frames);

    internal static long TotalAllocatedBytes
    {
        get
        {
            long total = 0;
            for (int i = 0; i < Allocated.Length; i++)
                total += Interlocked.Read(ref Allocated[i]);

            return total;
        }
    }

    internal static void Tick()
    {
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _lastSampleTick) < 1000)
            return;

        Volatile.Write(ref _lastSampleTick, now);
        TimeSpan processCpu;
        try
        {
            using var process = Process.GetCurrentProcess();
            processCpu = process.TotalProcessorTime;
        }
        catch (Exception) { processCpu = TimeSpan.Zero; }
        var sample = new Sample(Stopwatch.GetTimestamp(), Read(Ticks), Read(Allocated), Volatile.Read(ref _frames), processCpu);
        lock (SampleGate)
        {
            Samples[_nextSample] = sample;
            _nextSample = (_nextSample + 1) % SampleCount;
            _sampleCount = Math.Min(_sampleCount + 1, SampleCount);
        }
        GarbageCollection.Tick();
    }

    private static long[] Read(long[] values)
    {
        var copy = new long[values.Length];
        for (int i = 0; i < values.Length; i++)
            copy[i] = Interlocked.Read(ref values[i]);

        return copy;
    }

    internal sealed record AreaUsage(Area Area, double CpuPercentOfCore, double AllocatedBytesPerSecond,
        double TotalCpuSeconds, long TotalAllocatedBytes);

    internal sealed record Report(
        IReadOnlyList<AreaUsage> Areas,
        double WindowSeconds,
        double CpuPercentOfCore,
        double AllocatedBytesPerSecond,
        double HookMsPerFrame,
        double FramesPerSecond,
        double ShareOfProcessCpuPercent,
        double TotalCpuSeconds);

    internal static Report? GetReport()
    {
        Sample oldest, newest;
        lock (SampleGate)
        {
            if (_sampleCount < 2)
                return null;

            newest = Samples[(_nextSample - 1 + SampleCount) % SampleCount];
            oldest = Samples[(_nextSample - _sampleCount + SampleCount) % SampleCount];
        }
        double seconds = (newest.Timestamp - oldest.Timestamp) / (double)Stopwatch.Frequency;
        if (seconds <= 0)
            return null;

        var areas = new List<AreaUsage>(Areas.Length);
        double cpuSeconds = 0, allocated = 0, totalCpu = 0, hookSeconds = 0;
        foreach (Area area in Areas)
        {
            int i = (int)area;
            double areaCpu = (newest.Ticks[i] - oldest.Ticks[i]) / (double)Stopwatch.Frequency;
            double areaAllocated = newest.Allocated[i] - oldest.Allocated[i];
            double areaTotal = newest.Ticks[i] / (double)Stopwatch.Frequency;
            areas.Add(new AreaUsage(area, areaCpu / seconds * 100, areaAllocated / seconds, areaTotal, newest.Allocated[i]));
            cpuSeconds += areaCpu;
            allocated += areaAllocated;
            totalCpu += areaTotal;
            if (area is Area.Recording or Area.EngineHooks)
                hookSeconds += areaCpu;
        }
        long frames = newest.Frames - oldest.Frames;
        double processCpu = (newest.ProcessCpu - oldest.ProcessCpu).TotalSeconds;
        return new Report(
            areas,
            seconds,
            cpuSeconds / seconds * 100,
            allocated / seconds,
            frames > 0 ? hookSeconds * 1000 / frames : 0,
            frames / seconds,
            processCpu > 0 ? Math.Min(100, cpuSeconds / processCpu * 100) : 0,
            totalCpu);
    }
}
