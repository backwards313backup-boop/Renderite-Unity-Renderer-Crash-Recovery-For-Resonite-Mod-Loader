using System.Diagnostics;

namespace RenderiteRecovery;

internal static class GarbageCollection
{
    private const int SampleCount = 6;
    private const int Generations = 3;

    private sealed record Sample(long Timestamp, long Allocated, long Heap, int[] Collections);

    private static Sample? _start;
    private static readonly Sample[] Samples = new Sample[SampleCount];
    private static readonly object Gate = new();
    private static TimeSpan _startPause;
    private static int _count;
    private static int _next;

    internal static void Start()
    {
        lock (Gate)
        {
            _start = Take();
            _startPause = GC.GetTotalPauseDuration();
        }
    }

    internal static void Tick()
    {
        Sample sample = Take();
        lock (Gate)
        {
            Samples[_next] = sample;
            _next = (_next + 1) % SampleCount;
            _count = Math.Min(_count + 1, SampleCount);
        }
    }

    private static Sample Take()
    {
        var collections = new int[Generations];
        for (int generation = 0; generation < Generations; generation++)
            collections[generation] = GC.CollectionCount(generation);

        return new Sample(Stopwatch.GetTimestamp(), GC.GetTotalAllocatedBytes(false), GC.GetTotalMemory(false), collections);
    }

    internal sealed record Collection(
        long Number,
        string Kind,
        bool Compacted,
        double PauseMs,
        long HeapBeforeBytes,
        long HeapAfterBytes,
        long PromotedBytes,
        long FragmentedBytes,
        long[] GenerationAfterBytes);

    internal sealed record Report(
        int[] Collections,
        long AllocatedBytes,
        long ReclaimedBytes,
        double ReclaimedBytesPerSecond,
        double CollectionsPerMinute,
        TimeSpan Pause,
        double PausePercent,
        long HeapBytes,
        Collection? Last);

    internal static Report? GetReport()
    {
        Sample now = Take();
        Sample? start, oldest;
        TimeSpan startPause;
        lock (Gate)
        {
            start = _start;
            startPause = _startPause;
            oldest = _count == 0 ? null : Samples[(_next - _count + SampleCount) % SampleCount];
        }
        if (start is null)
            return null;

        var collections = new int[Generations];
        for (int generation = 0; generation < Generations; generation++)
            collections[generation] = now.Collections[generation] - start.Collections[generation];

        long allocated = now.Allocated - start.Allocated;
        long reclaimed = Math.Max(0, allocated - (now.Heap - start.Heap));

        double reclaimedPerSecond = 0, perMinute = 0;
        if (oldest is not null)
        {
            double seconds = (now.Timestamp - oldest.Timestamp) / (double)Stopwatch.Frequency;
            if (seconds >= 0.5)
            {
                long windowAllocated = now.Allocated - oldest.Allocated;
                reclaimedPerSecond = Math.Max(0, windowAllocated - (now.Heap - oldest.Heap)) / seconds;
                perMinute = (now.Collections[0] - oldest.Collections[0]) / seconds * 60;
            }
        }
        TimeSpan pause = GC.GetTotalPauseDuration() - startPause;
        double elapsed = (now.Timestamp - start.Timestamp) / (double)Stopwatch.Frequency;
        return new Report(collections, allocated, reclaimed, reclaimedPerSecond, perMinute, pause,
            elapsed > 0 ? pause.TotalSeconds / elapsed * 100 : 0, now.Heap, LastCollection());
    }

    private static Collection? LastCollection()
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo(GCKind.Any);
        if (info.Index == 0)
            return null;

        ReadOnlySpan<GCGenerationInfo> generations = info.GenerationInfo;
        long before = 0, after = 0;
        var afterBytes = new long[generations.Length];
        for (int i = 0; i < generations.Length; i++)
        {
            before += generations[i].SizeBeforeBytes;
            after += generations[i].SizeAfterBytes;
            afterBytes[i] = generations[i].SizeAfterBytes;
        }
        double pauseMs = 0;
        foreach (TimeSpan part in info.PauseDurations)
            pauseMs += part.TotalMilliseconds;

        string kind = info.Concurrent ? "background gen 2" : info.Generation >= 2 ? "full blocking gen 2" : $"gen {info.Generation}";
        return new Collection(info.Index, kind, info.Compacted, pauseMs, before, after, info.PromotedBytes,
            info.FragmentedBytes, afterBytes);
    }
}
