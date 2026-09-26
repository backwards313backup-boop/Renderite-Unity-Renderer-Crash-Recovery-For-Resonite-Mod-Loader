using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace RenderiteRecovery;

internal static class ResourceMetrics
{
    internal enum Area
    {
        Recording,
        EngineHooks,
        Background,
        Recovery,
        Panel,
        Telemetry
    }

    internal static readonly Area[] Areas = Enum.GetValues<Area>();
    private const int SampleCount = 6;
    private const int CommandSpacing = 128;

    private static readonly long[] Ticks = new long[Areas.Length];
    private static readonly long[] Cycles = new long[Areas.Length];
    private static readonly long[] Allocated = new long[Areas.Length];
    private static readonly Sample[] Samples = new Sample[SampleCount];
    private static readonly object SampleGate = new();
    private static long _engineTicks;
    private static int _engineThread = -1;
    private static int _sampleCount;
    private static int _nextSample;
    private static long _lastSampleTick;
    private static long _frames;
    [ThreadStatic] private static ThreadState? _thread;

    private sealed class ThreadState
    {
        internal int Depth;
        internal int Countdown;
        internal int Gap;
    }

    private sealed record Sample(long Timestamp, long[] Ticks, long[] Cycles, long[] Allocated, long EngineTicks, long Frames, TimeSpan ProcessCpu);

    internal readonly struct Scope : IDisposable
    {
        private readonly ThreadState _state;
        private readonly int _area;
        private readonly int _weight;
        private readonly long _start;
        private readonly long _cycles;
        private readonly long _allocatedStart;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Scope(Area area, bool everyTime)
        {
            ThreadState state = _thread ??= new ThreadState();
            _state = state;
            int weight = state.Depth++ > 0 ? 0 : everyTime ? 1 : --state.Countdown > 0 ? 0 : NextWeight(state);
            if (weight == 0)
            {
                _area = -1;
                _weight = 0;
                _start = _cycles = _allocatedStart = 0;
                return;
            }
            _area = (int)area;
            _weight = weight;
            Begin(out _start, out _cycles, out _allocatedStart);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose()
        {
            _state.Depth--;
            if (_area >= 0)
                Record();
        }

        private static void Begin(out long start, out long cycles, out long allocatedStart)
        {
            allocatedStart = GC.GetAllocatedBytesForCurrentThread();
            cycles = ThreadCpu.Cycles();
            start = Stopwatch.GetTimestamp();
        }

        private void Record()
        {
            long wall = (Stopwatch.GetTimestamp() - _start) * _weight;
            long end = _cycles >= 0 ? ThreadCpu.Cycles() : -1;
            Interlocked.Add(ref Ticks[_area], wall);
            if (end >= _cycles && _cycles >= 0)
                Interlocked.Add(ref Cycles[_area], (end - _cycles) * _weight);

            if (Environment.CurrentManagedThreadId == Volatile.Read(ref _engineThread))
                Interlocked.Add(ref _engineTicks, wall);

            Interlocked.Add(ref Allocated[_area], (GC.GetAllocatedBytesForCurrentThread() - _allocatedStart) * _weight);
        }
    }

    internal static Scope Measure(Area area) => new(area, true);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Scope MeasureCommand(Area area) => new(area, false);

    private static int NextWeight(ThreadState state)
    {
        int weight = Math.Max(1, state.Gap);
        state.Gap = state.Countdown = Random.Shared.Next(1, 2 * CommandSpacing);
        return weight;
    }

    internal static void CountFrame()
    {
        Volatile.Write(ref _engineThread, Environment.CurrentManagedThreadId);
        Interlocked.Increment(ref _frames);
    }

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
        var sample = new Sample(Stopwatch.GetTimestamp(), Read(Ticks), Read(Cycles), Read(Allocated),
            Interlocked.Read(ref _engineTicks), Volatile.Read(ref _frames), processCpu);
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

    internal sealed record AreaUsage(Area Area, double ShareOfProcessPercent, double TotalCpuSeconds,
        double AllocatedBytesPerSecond, long TotalAllocatedBytes);

    internal sealed record Report(
        IReadOnlyList<AreaUsage> Areas,
        double WindowSeconds,
        bool CpuMeasured,
        double ShareOfProcessPercent,
        double ShareOfMachinePercent,
        double ProcessShareOfMachinePercent,
        int LogicalProcessors,
        double EngineMsPerFrame,
        double EngineFramePercent,
        double WaitingThreads,
        double AllocatedBytesPerSecond,
        double FramesPerSecond,
        double TotalCpuSeconds);

    internal static Report? GetReport()
    {
        if (ThreadCpu.Supported && !ThreadCpu.Ready)
            return null;

        Sample oldest, newest;
        lock (SampleGate)
        {
            if (_sampleCount < 2)
                return null;

            newest = Samples[(_nextSample - 1 + SampleCount) % SampleCount];
            oldest = Samples[(_nextSample - _sampleCount + SampleCount) % SampleCount];
        }
        double frequency = Stopwatch.Frequency;
        double seconds = (newest.Timestamp - oldest.Timestamp) / frequency;
        if (seconds <= 0)
            return null;

        bool measured = ThreadCpu.Ready;
        double processCpu = (newest.ProcessCpu - oldest.ProcessCpu).TotalSeconds;
        var cpuByArea = new double[Areas.Length];
        double cpu = 0, wall = 0, allocated = 0, totalCpu = 0;
        foreach (Area area in Areas)
        {
            int i = (int)area;
            double areaWall = (newest.Ticks[i] - oldest.Ticks[i]) / frequency;
            cpuByArea[i] = measured ? ThreadCpu.Seconds(newest.Cycles[i] - oldest.Cycles[i]) : areaWall;
            cpu += cpuByArea[i];
            wall += areaWall;
            allocated += newest.Allocated[i] - oldest.Allocated[i];
            totalCpu += measured ? ThreadCpu.Seconds(newest.Cycles[i]) : newest.Ticks[i] / frequency;
        }
        var areas = new List<AreaUsage>(Areas.Length);
        foreach (Area area in Areas)
        {
            int i = (int)area;
            areas.Add(new AreaUsage(area, Share(cpuByArea[i], processCpu),
                measured ? ThreadCpu.Seconds(newest.Cycles[i]) : newest.Ticks[i] / frequency,
                (newest.Allocated[i] - oldest.Allocated[i]) / seconds, newest.Allocated[i]));
        }
        int processors = Math.Max(1, Environment.ProcessorCount);
        long frames = newest.Frames - oldest.Frames;
        double engineSeconds = (newest.EngineTicks - oldest.EngineTicks) / frequency;
        return new Report(
            areas,
            seconds,
            measured,
            Share(cpu, processCpu),
            Math.Min(100, cpu / (seconds * processors) * 100),
            Math.Min(100, processCpu / (seconds * processors) * 100),
            processors,
            frames > 0 ? engineSeconds * 1000 / frames : 0,
            Math.Min(100, engineSeconds / seconds * 100),
            measured ? Math.Max(0, wall - cpu) / seconds : 0,
            allocated / seconds,
            frames / seconds,
            totalCpu);
    }

    private static double Share(double part, double whole) => whole > 0 ? Math.Min(100, part / whole * 100) : 0;
}
