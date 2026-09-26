using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RenderiteRecovery;

internal static class ThreadCpu
{
    private const nint CurrentThreadHandle = -2;

    private static readonly bool Available = Probe();
    private static double _cyclesPerSecond;
    private static int _started;

    internal static bool Supported => Available;

    internal static double CyclesPerSecond => Volatile.Read(ref _cyclesPerSecond);

    internal static bool Ready => Available && CyclesPerSecond > 0;

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryThreadCycleTime(nint thread, out ulong cycles);

    internal static long Cycles()
    {
        if (!Available)
            return -1;

        return QueryThreadCycleTime(CurrentThreadHandle, out ulong cycles) ? (long)cycles : -1;
    }

    internal static double Seconds(long cycles)
    {
        double perSecond = CyclesPerSecond;
        return perSecond > 0 ? cycles / perSecond : 0;
    }

    internal static void Start()
    {
        if (!Available || Interlocked.Exchange(ref _started, 1) != 0)
            return;

        new Thread(Calibrate) { IsBackground = true, Name = "RenderiteRecovery CPU clock calibration" }.Start();
    }

    private static bool Probe()
    {
        try
        {
            return OperatingSystem.IsWindows() && QueryThreadCycleTime(CurrentThreadHandle, out _);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Calibrate()
    {
        double best = 0;
        try
        {
            for (int run = 0; run < 3; run++)
            {
                if (!QueryThreadCycleTime(CurrentThreadHandle, out ulong startCycles))
                    return;

                long start = Stopwatch.GetTimestamp();
                long end = start + Stopwatch.Frequency / 50;
                while (Stopwatch.GetTimestamp() < end) { }
                long stop = Stopwatch.GetTimestamp();
                if (!QueryThreadCycleTime(CurrentThreadHandle, out ulong stopCycles))
                    return;

                double seconds = (stop - start) / (double)Stopwatch.Frequency;
                best = Math.Max(best, (stopCycles - startCycles) / seconds);
            }
        }
        catch (Exception)
        {
            return;
        }
        Volatile.Write(ref _cyclesPerSecond, best);
    }
}
