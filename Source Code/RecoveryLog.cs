using System.Collections.Concurrent;
using System.Text;

namespace RenderiteRecovery;

internal static class RecoveryLog
{
    private const int MaximumQueued = 20_000;
    private static readonly TimeSpan ExitFlushTimeout = TimeSpan.FromSeconds(2);

    private static readonly ConcurrentQueue<string> Pending = new();
    private static readonly AutoResetEvent Signal = new(false);
    private static readonly object WriteGate = new();
    private static string? _path;
    private static int _queued;
    private static int _dropped;
    private static int _started;

    internal static void Write(string message)
    {
        if (!Settings.WriteLogFiles)
            return;

        if (_path is null)
        {
            string? appPath = FrooxEngine.Engine.Current?.AppPath;
            if (appPath is null)
                return;

            _path = Path.Combine(appPath, "Logs", "RenderiteRecovery.Host.log");
        }
        if (Interlocked.Increment(ref _queued) > MaximumQueued)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _dropped);
            return;
        }
        Pending.Enqueue(Line(message));
        EnsureStarted();
        Signal.Set();
    }

    internal static void Flush(TimeSpan timeout)
    {
        if (!Monitor.TryEnter(WriteGate, timeout))
            return;

        try { WritePending(); }
        finally { Monitor.Exit(WriteGate); }
    }

    private static string Line(string message) => $"{DateTime.UtcNow:O} PID={Environment.ProcessId} {message}";

    private static void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Write($"ERROR: Resonite is terminating on an unhandled exception: {args.ExceptionObject}");
            Flush(ExitFlushTimeout);
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush(ExitFlushTimeout);
        new Thread(Run) { IsBackground = true, Name = "RenderiteRecovery log writer" }.Start();
    }

    private static void Run()
    {
        while (true)
        {
            Signal.WaitOne();
            lock (WriteGate)
                WritePending();
        }
    }

    private static void WritePending()
    {
        string? path = _path;
        if (path is null || Pending.IsEmpty)
            return;

        var batch = new StringBuilder();
        int count = 0;
        while (Pending.TryDequeue(out string? line))
        {
            batch.AppendLine(line);
            count++;
        }
        int dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0)
            batch.AppendLine(Line($"WARN: {dropped} log lines were dropped because the disk could not keep up."));

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var writer = new StreamWriter(path, append: true);
            writer.Write(batch);
        }
        catch (Exception) { }
        finally { Interlocked.Add(ref _queued, -count); }
    }
}
