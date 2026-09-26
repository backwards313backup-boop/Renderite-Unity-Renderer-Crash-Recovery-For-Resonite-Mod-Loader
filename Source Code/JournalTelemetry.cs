using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using FrooxEngine;
using Renderite.Shared;

namespace RenderiteRecovery;

internal static class JournalTelemetry
{
    internal enum Removal { Replaced, Rewritten, Unloaded }

    internal enum Stage { Capture, ArchiveWait, JournalWait, JournalHeld, Encode }

    private const long ReportIntervalMs = 10_000;
    private const int MaximumTrackedAssets = 20_000;
    private const int ReportedAssets = 40;
    private const int ReportedChurn = 30;
    private const int TimelineRows = 90;
    private const int MaximumReportFailures = 3;
    private const string FileName = "RenderiteRecovery.JournalTelemetry.txt";

    private static volatile bool _enabled;
    private static long _enabledTimestamp;
    private static DateTime _enabledLocal;
    private static readonly ConcurrentDictionary<string, TypeCounters> Types = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<(string Family, int AssetId), AssetCounters> Assets = new();
    private static int _trackedAssets;
    private static long _untrackedAssetEvents;
    private static long _nextReport;
    private static int _reportQueued;
    private static int _reportFailures;
    private static Dictionary<string, TypeCounters> _previousTypes = new(StringComparer.Ordinal);
    private static Dictionary<(string Family, int AssetId), AssetCounters> _previousAssets = new();
    private static long _previousTimestamp;
    private static TimeSpan _previousProcessCpu;
    private static readonly Queue<TimelineRow> Timeline = new();
    private static readonly object ReportGate = new();
    private static readonly object FileGate = new();

    [ThreadStatic] private static long _captureTicks;
    [ThreadStatic] private static long _archiveWaitTicks;
    [ThreadStatic] private static long _journalWaitTicks;
    [ThreadStatic] private static long _journalHeldTicks;
    [ThreadStatic] private static long _encodeTicks;

    private sealed class TypeCounters
    {
        internal long Sent, SentTicks, SentCycles, CaptureTicks, ArchiveWaitTicks, JournalWaitTicks, JournalHeldTicks, EncodeTicks;
        internal long Journaled, JournaledBytes, CopiedBytes, Replaced, Rewritten, Unloaded, RemovedBytes, LifetimeTicks, Identical;

        internal TypeCounters Copy() => new()
        {
            Sent = Interlocked.Read(ref Sent), SentTicks = Interlocked.Read(ref SentTicks),
            SentCycles = Interlocked.Read(ref SentCycles), CaptureTicks = Interlocked.Read(ref CaptureTicks),
            ArchiveWaitTicks = Interlocked.Read(ref ArchiveWaitTicks), JournalWaitTicks = Interlocked.Read(ref JournalWaitTicks),
            JournalHeldTicks = Interlocked.Read(ref JournalHeldTicks), EncodeTicks = Interlocked.Read(ref EncodeTicks),
            Journaled = Interlocked.Read(ref Journaled), JournaledBytes = Interlocked.Read(ref JournaledBytes),
            CopiedBytes = Interlocked.Read(ref CopiedBytes),
            Replaced = Interlocked.Read(ref Replaced), Rewritten = Interlocked.Read(ref Rewritten),
            Unloaded = Interlocked.Read(ref Unloaded), RemovedBytes = Interlocked.Read(ref RemovedBytes),
            LifetimeTicks = Interlocked.Read(ref LifetimeTicks), Identical = Interlocked.Read(ref Identical)
        };

        internal long Removed => Replaced + Rewritten + Unloaded;
    }

    private sealed class AssetCounters
    {
        internal long Journaled, JournaledBytes, Removed, Unloads, Reloads, Identical;
        internal int UnloadedLast;
        internal volatile string LastType = "";

        internal AssetCounters Copy() => new()
        {
            Journaled = Interlocked.Read(ref Journaled), JournaledBytes = Interlocked.Read(ref JournaledBytes),
            Removed = Interlocked.Read(ref Removed), Unloads = Interlocked.Read(ref Unloads),
            Reloads = Interlocked.Read(ref Reloads), Identical = Interlocked.Read(ref Identical), LastType = LastType
        };
    }

    private sealed record TimelineRow(DateTime Local, double Sent, double Journaled, double Removed, double Bytes, double Copied,
        double ThreadsInside, double CpuOfResonite, double WaitShare, int LiveEntries, long LiveBytes);

    internal readonly struct Probe
    {
        internal readonly long Start;
        internal readonly long Cycles;

        internal Probe(long start, long cycles)
        {
            Start = start;
            Cycles = cycles;
        }
    }

    internal static bool Enabled => _enabled;

    internal static bool MeasuresCpu => ThreadCpu.Ready;

    internal static string? ReportPath
    {
        get
        {
            string? appPath = Engine.Current?.AppPath;
            return appPath is null ? null : Path.Combine(appPath, "Logs", FileName);
        }
    }

    internal static void SetEnabled(bool enabled)
    {
        lock (ReportGate)
        {
            if (enabled == _enabled)
                return;

            Reset();
            if (enabled)
            {
                _enabledTimestamp = Stopwatch.GetTimestamp();
                _previousTimestamp = _enabledTimestamp;
                _enabledLocal = DateTime.Now;
                _previousProcessCpu = ProcessCpu();
                _nextReport = Environment.TickCount64 + ReportIntervalMs;
                ThreadCpu.Start();
            }
            _enabled = enabled;
        }
        RenderiteRecoveryMod.Msg(enabled
            ? (Settings.WriteLogFiles
                ? $"Journal telemetry is on. The report is rewritten every {RenderiteRecoveryMod.Seconds(ReportIntervalMs / 1000)} in Logs/{FileName}."
                : "Journal telemetry is on, but write_log_files is false, so no report is written.")
            : "Journal telemetry is off.");
    }

    private static void Reset()
    {
        Types.Clear();
        Assets.Clear();
        Volatile.Write(ref _trackedAssets, 0);
        Interlocked.Exchange(ref _untrackedAssetEvents, 0);
        _previousTypes = new Dictionary<string, TypeCounters>(StringComparer.Ordinal);
        _previousAssets = new Dictionary<(string Family, int AssetId), AssetCounters>();
        Timeline.Clear();
        _reportFailures = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Probe Begin() => _enabled ? Start() : default;

    private static Probe Start()
    {
        _captureTicks = _archiveWaitTicks = _journalWaitTicks = _journalHeldTicks = _encodeTicks = 0;
        return new Probe(Stopwatch.GetTimestamp(), ThreadCpu.Cycles());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void End(in Probe probe, RendererCommand command)
    {
        if (probe.Start != 0)
            Finish(probe, command);
    }

    private static void Finish(in Probe probe, RendererCommand command)
    {
        if (!_enabled)
            return;

        long ticks = Stopwatch.GetTimestamp() - probe.Start;
        long cycles = probe.Cycles >= 0 && ThreadCpu.Cycles() is long now && now >= probe.Cycles ? now - probe.Cycles : 0;
        TypeCounters counters = TypeFor(command.GetType().Name);
        Interlocked.Increment(ref counters.Sent);
        Interlocked.Add(ref counters.SentTicks, ticks);
        Interlocked.Add(ref counters.SentCycles, cycles);
        Interlocked.Add(ref counters.CaptureTicks, _captureTicks);
        Interlocked.Add(ref counters.ArchiveWaitTicks, _archiveWaitTicks);
        Interlocked.Add(ref counters.JournalWaitTicks, _journalWaitTicks);
        Interlocked.Add(ref counters.JournalHeldTicks, _journalHeldTicks);
        Interlocked.Add(ref counters.EncodeTicks, _encodeTicks);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long Now() => _enabled ? Stopwatch.GetTimestamp() : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long Add(Stage stage, long start) => start == 0 ? 0 : Accumulate(stage, start);

    private static long Accumulate(Stage stage, long start)
    {
        long now = Stopwatch.GetTimestamp();
        long elapsed = now - start;
        switch (stage)
        {
            case Stage.Capture: _captureTicks += elapsed; break;
            case Stage.ArchiveWait: _archiveWaitTicks += elapsed; break;
            case Stage.JournalWait: _journalWaitTicks += elapsed; break;
            case Stage.JournalHeld: _journalHeldTicks += elapsed; break;
            case Stage.Encode: _encodeTicks += elapsed; break;
        }
        return now;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Journaled(CommandJournal.Entry entry)
    {
        if (_enabled)
            RecordJournaled(entry);
    }

    private static void RecordJournaled(CommandJournal.Entry entry)
    {
        long bytes = entry.Payload.LongLength + entry.BufferBytes;
        TypeCounters counters = TypeFor(entry.Type);
        Interlocked.Increment(ref counters.Journaled);
        Interlocked.Add(ref counters.JournaledBytes, bytes);
        Interlocked.Add(ref counters.CopiedBytes, entry.Payload.LongLength + entry.ArchivedBytes);
        if (entry.AssetId is not int assetId || AssetFor(FamilyOf(entry.Type), assetId) is not AssetCounters asset)
            return;

        Interlocked.Increment(ref asset.Journaled);
        Interlocked.Add(ref asset.JournaledBytes, bytes);
        asset.LastType = entry.Type;
        if (Interlocked.Exchange(ref asset.UnloadedLast, 0) == 1)
            Interlocked.Increment(ref asset.Reloads);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Removed(CommandJournal.Entry entry, Removal reason)
    {
        if (_enabled)
            RecordRemoved(entry, reason);
    }

    private static void RecordRemoved(CommandJournal.Entry entry, Removal reason)
    {
        TypeCounters counters = TypeFor(entry.Type);
        switch (reason)
        {
            case Removal.Replaced: Interlocked.Increment(ref counters.Replaced); break;
            case Removal.Rewritten: Interlocked.Increment(ref counters.Rewritten); break;
            case Removal.Unloaded: Interlocked.Increment(ref counters.Unloaded); break;
        }
        Interlocked.Add(ref counters.RemovedBytes, entry.Payload.LongLength + entry.BufferBytes);
        Interlocked.Add(ref counters.LifetimeTicks, Stopwatch.GetTimestamp() - entry.RecordedTimestamp);
        if (entry.AssetId is int assetId && AssetFor(FamilyOf(entry.Type), assetId) is AssetCounters asset)
            Interlocked.Increment(ref asset.Removed);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Identical(string type, int assetId)
    {
        if (_enabled)
            RecordIdentical(type, assetId);
    }

    private static void RecordIdentical(string type, int assetId)
    {
        Interlocked.Increment(ref TypeFor(type).Identical);
        if (AssetFor(FamilyOf(type), assetId) is AssetCounters asset)
            Interlocked.Increment(ref asset.Identical);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Unloaded(string family, int assetId)
    {
        if (_enabled)
            RecordUnloaded(family, assetId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void StreamUnloaded(string type, int assetId)
    {
        if (_enabled)
            RecordUnloaded(FamilyOf(type), assetId);
    }

    private static void RecordUnloaded(string family, int assetId)
    {
        if (AssetFor(family, assetId) is not AssetCounters asset)
            return;

        Interlocked.Increment(ref asset.Unloads);
        Volatile.Write(ref asset.UnloadedLast, 1);
    }

    internal static string FamilyOf(string type) => AssetQuarantine.Family(type) ?? type switch
    {
        nameof(SetRenderTextureFormat) or nameof(UnloadRenderTexture) => "RenderTexture",
        nameof(SetDesktopTextureProperties) or nameof(UnloadDesktopTexture) => "DesktopTexture",
        _ => type
    };

    private static TypeCounters TypeFor(string type) => Types.GetOrAdd(type, static _ => new TypeCounters());

    private static AssetCounters? AssetFor(string family, int assetId)
    {
        if (Assets.TryGetValue((family, assetId), out AssetCounters? counters))
            return counters;

        if (Volatile.Read(ref _trackedAssets) >= MaximumTrackedAssets)
        {
            Interlocked.Increment(ref _untrackedAssetEvents);
            return null;
        }
        counters = new AssetCounters();
        if (Assets.TryAdd((family, assetId), counters))
        {
            Interlocked.Increment(ref _trackedAssets);
            return counters;
        }
        return Assets.TryGetValue((family, assetId), out AssetCounters? existing) ? existing : null;
    }

    internal static void Poll()
    {
        if (!_enabled)
            return;

        long now = Environment.TickCount64;
        if (now < Volatile.Read(ref _nextReport) || Volatile.Read(ref _reportFailures) >= MaximumReportFailures
            || !Settings.WriteLogFiles)
            return;

        Volatile.Write(ref _nextReport, now + ReportIntervalMs);
        World? world = Userspace.UserspaceWorld;
        if (world is null || world.IsDestroyed || Interlocked.Exchange(ref _reportQueued, 1) != 0)
            return;

        world.RunSynchronously(() =>
        {
            using var measure = ResourceMetrics.Measure(ResourceMetrics.Area.Telemetry);
            try
            {
                Publish();
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref _reportFailures) >= MaximumReportFailures)
                    RenderiteRecoveryMod.Warn($"Journal telemetry reports are stopped for this session after repeated errors: {ex}");
                else
                    RenderiteRecoveryMod.Warn($"Could not build the journal telemetry report: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _reportQueued, 0);
            }
        });
    }

    private sealed record TypeRow(string Type, TypeCounters Total, TypeCounters Interval);

    private sealed record AssetRow((string Family, int AssetId) Key, AssetCounters Total, AssetCounters Interval);

    private static void Publish()
    {
        string? path = ReportPath;
        if (path is null)
            return;

        string text;
        lock (ReportGate)
        {
            if (!_enabled)
                return;

            long now = Stopwatch.GetTimestamp();
            double interval = Math.Max(0.001, (now - _previousTimestamp) / (double)Stopwatch.Frequency);
            double enabledFor = Math.Max(0.001, (now - _enabledTimestamp) / (double)Stopwatch.Frequency);

            var typeRows = new List<TypeRow>();
            var currentTypes = new Dictionary<string, TypeCounters>(StringComparer.Ordinal);
            foreach ((string type, TypeCounters counters) in Types)
            {
                TypeCounters total = counters.Copy();
                currentTypes[type] = total;
                typeRows.Add(new TypeRow(type, total, Minus(total, _previousTypes.GetValueOrDefault(type))));
            }
            var assetRows = new List<AssetRow>();
            var currentAssets = new Dictionary<(string Family, int AssetId), AssetCounters>();
            foreach (((string Family, int AssetId) key, AssetCounters counters) in Assets)
            {
                AssetCounters total = counters.Copy();
                currentAssets[key] = total;
                assetRows.Add(new AssetRow(key, total, Minus(total, _previousAssets.GetValueOrDefault(key))));
            }

            CommandJournal.Composition? composition = RecoveryCoordinator.JournalComposition();
            List<CommandJournal.AssetShare> largest = composition?.Assets.Take(ReportedAssets).ToList() ?? [];
            List<AssetRow> churn = assetRows.Where(row => row.Interval.Journaled + row.Interval.Removed > 0)
                .OrderByDescending(row => row.Interval.Journaled + row.Interval.Removed)
                .ThenByDescending(row => row.Interval.JournaledBytes).Take(ReportedChurn).ToList();
            List<AssetRow> reloaded = assetRows.Where(row => row.Total.Reloads > 0)
                .OrderByDescending(row => row.Total.Reloads).Take(ReportedChurn).ToList();
            List<AssetRow> identical = assetRows.Where(row => row.Total.Identical > 0)
                .OrderByDescending(row => row.Total.Identical).Take(10).ToList();

            var wanted = largest.Select(share => (share.Family, share.AssetId))
                .Concat(churn.Select(row => row.Key)).Concat(reloaded.Select(row => row.Key)).Concat(identical.Select(row => row.Key));
            Dictionary<(string Family, int AssetId), string> names = AssetNames.Resolve(Engine.Current?.RenderSystem, wanted);

            TypeCounters sum = Sum(typeRows.Select(row => row.Interval));
            TimeSpan processCpuNow = ProcessCpu();
            double processCpu = Math.Max(0, (processCpuNow - _previousProcessCpu).TotalSeconds);
            int processors = Math.Max(1, Environment.ProcessorCount);
            double threadsInside = sum.SentTicks / (double)Stopwatch.Frequency / interval;
            double cpuOfResonite = MeasuresCpu ? Share(ThreadCpu.Seconds(sum.SentCycles), processCpu) : double.NaN;
            double cpuOfMachine = MeasuresCpu ? Math.Min(100, ThreadCpu.Seconds(sum.SentCycles) / (interval * processors) * 100) : double.NaN;
            double waitShare = Share(sum.JournalWaitTicks + sum.ArchiveWaitTicks, sum.SentTicks);
            Timeline.Enqueue(new TimelineRow(DateTime.Now, sum.Sent / interval, sum.Journaled / interval, sum.Removed / interval,
                sum.JournaledBytes / interval, sum.CopiedBytes / interval, threadsInside, cpuOfResonite, waitShare,
                composition?.LiveEntries ?? 0, (composition?.CommandBytes ?? 0) + (composition?.PayloadBytes ?? 0)));
            while (Timeline.Count > TimelineRows)
                Timeline.Dequeue();

            var context = new ReportContext(interval, enabledFor, composition, typeRows, largest, churn, reloaded, identical, names,
                sum, processCpu, processors, threadsInside, cpuOfResonite, cpuOfMachine, waitShare);
            text = FormatReport(context);

            _previousTypes = currentTypes;
            _previousAssets = currentAssets;
            _previousTimestamp = now;
            _previousProcessCpu = processCpuNow;
        }

        _ = Task.Run(() => WriteReport(path, text));
    }

    private static TimeSpan ProcessCpu()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.TotalProcessorTime;
        }
        catch (Exception)
        {
            return TimeSpan.Zero;
        }
    }

    private static double Share(double part, double whole) => whole > 0 ? Math.Min(100, part / whole * 100) : 0;

    private static void WriteReport(string path, string text)
    {
        lock (FileGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, text);
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref _reportFailures) < MaximumReportFailures)
                    RenderiteRecoveryMod.Warn($"Could not write {path}: {ex.Message}");
            }
        }
    }

    private static TypeCounters Minus(TypeCounters total, TypeCounters? previous) => previous is null ? total : new TypeCounters
    {
        Sent = total.Sent - previous.Sent, SentTicks = total.SentTicks - previous.SentTicks,
        SentCycles = total.SentCycles - previous.SentCycles, CaptureTicks = total.CaptureTicks - previous.CaptureTicks,
        ArchiveWaitTicks = total.ArchiveWaitTicks - previous.ArchiveWaitTicks,
        JournalWaitTicks = total.JournalWaitTicks - previous.JournalWaitTicks,
        JournalHeldTicks = total.JournalHeldTicks - previous.JournalHeldTicks, EncodeTicks = total.EncodeTicks - previous.EncodeTicks,
        Journaled = total.Journaled - previous.Journaled, JournaledBytes = total.JournaledBytes - previous.JournaledBytes,
        CopiedBytes = total.CopiedBytes - previous.CopiedBytes,
        Replaced = total.Replaced - previous.Replaced, Rewritten = total.Rewritten - previous.Rewritten,
        Unloaded = total.Unloaded - previous.Unloaded, RemovedBytes = total.RemovedBytes - previous.RemovedBytes,
        LifetimeTicks = total.LifetimeTicks - previous.LifetimeTicks, Identical = total.Identical - previous.Identical
    };

    private static AssetCounters Minus(AssetCounters total, AssetCounters? previous) => previous is null ? total : new AssetCounters
    {
        Journaled = total.Journaled - previous.Journaled, JournaledBytes = total.JournaledBytes - previous.JournaledBytes,
        Removed = total.Removed - previous.Removed, Unloads = total.Unloads - previous.Unloads,
        Reloads = total.Reloads - previous.Reloads, Identical = total.Identical - previous.Identical, LastType = total.LastType
    };

    private static TypeCounters Sum(IEnumerable<TypeCounters> rows)
    {
        var sum = new TypeCounters();
        foreach (TypeCounters row in rows)
        {
            sum.Sent += row.Sent; sum.SentTicks += row.SentTicks; sum.SentCycles += row.SentCycles;
            sum.CaptureTicks += row.CaptureTicks; sum.ArchiveWaitTicks += row.ArchiveWaitTicks;
            sum.JournalWaitTicks += row.JournalWaitTicks; sum.JournalHeldTicks += row.JournalHeldTicks;
            sum.EncodeTicks += row.EncodeTicks; sum.Journaled += row.Journaled; sum.JournaledBytes += row.JournaledBytes;
            sum.CopiedBytes += row.CopiedBytes;
            sum.Replaced += row.Replaced; sum.Rewritten += row.Rewritten; sum.Unloaded += row.Unloaded;
            sum.RemovedBytes += row.RemovedBytes; sum.LifetimeTicks += row.LifetimeTicks; sum.Identical += row.Identical;
        }
        return sum;
    }

    private sealed record ReportContext(double Interval, double EnabledFor, CommandJournal.Composition? Composition,
        List<TypeRow> Types, List<CommandJournal.AssetShare> Largest, List<AssetRow> Churn, List<AssetRow> Reloaded,
        List<AssetRow> Identical, Dictionary<(string Family, int AssetId), string> Names, TypeCounters Sum,
        double ProcessCpu, int Processors, double ThreadsInside, double CpuOfResonite, double CpuOfMachine, double WaitShare);

    private static string FormatReport(ReportContext report)
    {
        var text = new StringBuilder();
        double frequency = Stopwatch.Frequency;
        double interval = report.Interval;
        string window = RenderiteRecoveryMod.Seconds(Math.Max(1, (long)Math.Round(interval)));
        text.AppendLine("Renderite Recovery journal telemetry");
        text.AppendLine($"Written {DateTime.Now:yyyy-MM-dd HH:mm:ss}. Telemetry has been on since {_enabledLocal:HH:mm:ss} ({Duration(report.EnabledFor)}). This file is rewritten every {RenderiteRecoveryMod.Seconds(ReportIntervalMs / 1000)}.");
        text.AppendLine("Turn it off with the Journal telemetry button on the dash Recovery screen or journal_telemetry in the settings file.");
        text.AppendLine();

        Heading(text, "Journaling hook, last " + window);
        text.AppendLine($"Commands through the hook: {Rate(report.Sum.Sent, interval)} per second. Journaled: {Rate(report.Sum.Journaled, interval)} per second ({Size((long)(report.Sum.JournaledBytes / interval))} per second of commands and data, of which {Size((long)(report.Sum.CopiedBytes / interval))} per second is copied into the journal and archive, the rest stays in the engine's memory and is read when a recovery starts). Removed again: {Rate(report.Sum.Removed, interval)} per second.");
        text.AppendLine(double.IsNaN(report.CpuOfResonite)
            ? "CPU used inside the hook: not measured on this system."
            : $"CPU used inside the hook: {Percent(report.CpuOfResonite)} of Resonite's CPU time, {Percent(report.CpuOfMachine)} of the whole CPU ({report.Processors} threads).");
        text.AppendLine($"Threads inside the hook: {report.ThreadsInside:F2} on average. This counts time spent waiting, so it is not CPU use.");
        long hookTicks = report.Sum.SentTicks;
        text.AppendLine("Where the time inside the hook goes:");
        text.AppendLine($"  waiting for the journal lock: {Percent(Share(report.Sum.JournalWaitTicks, hookTicks))}");
        text.AppendLine($"  holding the journal lock: {Percent(Share(report.Sum.JournalHeldTicks, hookTicks))}");
        text.AppendLine($"  capturing payloads: {Percent(Share(report.Sum.CaptureTicks, hookTicks))} (finding the data buffers and hashing and copying the ones the archive keeps)");
        text.AppendLine($"  waiting for the archive (lock or disk backlog): {Percent(Share(report.Sum.ArchiveWaitTicks, hookTicks))}");
        text.AppendLine($"  encoding commands: {Percent(Share(report.Sum.EncodeTicks, hookTicks))}");
        text.AppendLine();

        Heading(text, "What the journal holds now");
        if (report.Composition is not CommandJournal.Composition composition)
            text.AppendLine("The journal is off for this session, so there is nothing to show.");
        else
        {
            text.AppendLine($"{composition.LiveEntries:N0} live entries: {Size(composition.CommandBytes)} of commands and {Size(composition.PayloadBytes)} of payloads (mesh, texture and buffer data).");
            text.AppendLine($"{composition.ListedEntries - composition.LiveEntries:N0} replaced entries are waiting to be cleared from the list. {composition.NoAssetEntries:N0} entries belong to no asset. Startup data: {Size(composition.InitializationBytes)}.");
            if (composition.LongestHistory > 0)
                text.AppendLine($"Longest history of a single asset: {composition.LongestHistoryAsset} with {composition.LongestHistory:N0} {(composition.LongestHistory == 1 ? "entry" : "entries")}.");

            text.AppendLine();
            long total = Math.Max(1, composition.CommandBytes + composition.PayloadBytes);
            var rows = composition.Types.Select(share => new[]
            {
                share.Type, share.Entries.ToString("N0", CultureInfo.InvariantCulture), Size(share.CommandBytes), Size(share.PayloadBytes),
                Percent((share.CommandBytes + share.PayloadBytes) * 100.0 / total)
            }).ToList();
            Table(text, ["Command type", "Entries", "Commands", "Payloads", "Share"], rows);
        }
        text.AppendLine();

        Heading(text, $"Largest items in the journal (top {ReportedAssets})");
        if (report.Largest.Count == 0)
            text.AppendLine("None.");
        else
        {
            var rows = report.Largest.Select(share => new[]
            {
                Size(share.Bytes), share.Entries.ToString("N0", CultureInfo.InvariantCulture), $"{share.Family} {share.AssetId}",
                share.Types, report.Names.GetValueOrDefault((share.Family, share.AssetId), "not found")
            }).ToList();
            Table(text, ["Size", "Entries", "Asset", "Commands", "What it is"], rows);
        }
        text.AppendLine();

        Heading(text, $"Traffic per command type, last {window} (per second)");
        var traffic = report.Types.Where(row => row.Interval.Sent + row.Interval.Journaled + row.Interval.Removed > 0)
            .OrderByDescending(row => row.Interval.SentTicks).ThenByDescending(row => row.Interval.Sent).ToList();
        if (traffic.Count == 0)
            text.AppendLine("No commands in this window.");
        else
        {
            var rows = traffic.Select(row => new[]
            {
                row.Type, Rate(row.Interval.Sent, interval), Rate(row.Interval.Journaled, interval),
                Rate(row.Interval.Replaced + row.Interval.Rewritten, interval), Rate(row.Interval.Unloaded, interval),
                Size((long)(row.Interval.JournaledBytes / interval)), Size((long)(row.Interval.CopiedBytes / interval)), Lifetime(row.Interval),
                Percent(Share(row.Interval.SentTicks, report.Sum.SentTicks)),
                MeasuresCpu ? Percent(Share(ThreadCpu.Seconds(row.Interval.SentCycles), report.ProcessCpu)) : "-",
                Percent(Share(row.Interval.JournalWaitTicks + row.Interval.ArchiveWaitTicks, row.Interval.SentTicks)),
                Percent(Share(row.Interval.CaptureTicks, row.Interval.SentTicks))
            }).ToList();
            Table(text, ["Command type", "Sent", "Journaled", "Replaced", "Unloaded", "Bytes", "Copied", "Average life", "Share of hook time", "CPU, % of Resonite", "Waiting", "Capturing"], rows);
        }
        text.AppendLine();

        Heading(text, "Traffic per command type since telemetry was turned on");
        var totals = report.Types.OrderByDescending(row => row.Total.SentTicks).ToList();
        if (totals.Count == 0)
            text.AppendLine("Nothing yet.");
        else
        {
            var rows = totals.Select(row => new[]
            {
                row.Type, row.Total.Sent.ToString("N0", CultureInfo.InvariantCulture), row.Total.Journaled.ToString("N0", CultureInfo.InvariantCulture),
                row.Total.Replaced.ToString("N0", CultureInfo.InvariantCulture), row.Total.Rewritten.ToString("N0", CultureInfo.InvariantCulture),
                row.Total.Unloaded.ToString("N0", CultureInfo.InvariantCulture), row.Total.Identical.ToString("N0", CultureInfo.InvariantCulture),
                Size(row.Total.JournaledBytes), Size(row.Total.CopiedBytes), Size(row.Total.RemovedBytes), $"{row.Total.SentTicks / frequency:F1} seconds",
                MeasuresCpu ? $"{ThreadCpu.Seconds(row.Total.SentCycles):F1} seconds" : "-"
            }).ToList();
            Table(text, ["Command type", "Sent", "Journaled", "Replaced", "Rewritten", "Unloaded", "Identical, dropped", "Bytes in", "Copied in", "Bytes out", "Time inside", "CPU time"], rows);
        }
        text.AppendLine();

        Heading(text, $"Items journaled or removed most often, last {window}");
        if (report.Churn.Count == 0)
            text.AppendLine("None.");
        else
        {
            var rows = report.Churn.Select(row => new[]
            {
                Rate(row.Interval.Journaled, interval), Rate(row.Interval.Removed, interval), Size((long)(row.Interval.JournaledBytes / interval)),
                row.Total.Journaled.ToString("N0", CultureInfo.InvariantCulture), $"{row.Key.Family} {row.Key.AssetId}", row.Total.LastType,
                report.Names.GetValueOrDefault(row.Key, "not found")
            }).ToList();
            Table(text, ["Journaled per second", "Removed per second", "Bytes per second", "Journaled in total", "Asset", "Last command", "What it is"], rows);
        }
        text.AppendLine();

        Heading(text, "Items unloaded and then loaded again, since telemetry was turned on");
        if (report.Reloaded.Count == 0)
            text.AppendLine("None.");
        else
        {
            var rows = report.Reloaded.Select(row => new[]
            {
                row.Total.Reloads.ToString("N0", CultureInfo.InvariantCulture), row.Total.Unloads.ToString("N0", CultureInfo.InvariantCulture),
                Size(row.Total.JournaledBytes), $"{row.Key.Family} {row.Key.AssetId}", report.Names.GetValueOrDefault(row.Key, "not found (unloaded now)")
            }).ToList();
            Table(text, ["Reloads", "Unloads", "Bytes journaled", "Asset", "What it is"], rows);
        }
        text.AppendLine();

        if (report.Identical.Count > 0)
        {
            Heading(text, "Items the engine re-sent unchanged (dropped by the journal)");
            var rows = report.Identical.Select(row => new[]
            {
                row.Total.Identical.ToString("N0", CultureInfo.InvariantCulture), $"{row.Key.Family} {row.Key.AssetId}",
                report.Names.GetValueOrDefault(row.Key, "not found")
            }).ToList();
            Table(text, ["Times", "Asset", "What it is"], rows);
            text.AppendLine();
        }

        long untracked = Interlocked.Read(ref _untrackedAssetEvents);
        if (untracked > 0)
            text.AppendLine($"Note: more than {MaximumTrackedAssets:N0} different items were seen, so {untracked:N0} events for newer items are counted per command type only.");

        Heading(text, $"Timeline (one row per report, newest last, up to {TimelineRows})");
        var timeline = Timeline.Select(row => new[]
        {
            row.Local.ToString("HH:mm:ss", CultureInfo.InvariantCulture), Rate(row.Sent, 1), Rate(row.Journaled, 1), Rate(row.Removed, 1),
            Size((long)row.Bytes), Size((long)row.Copied), row.ThreadsInside.ToString("F2", CultureInfo.InvariantCulture),
            double.IsNaN(row.CpuOfResonite) ? "-" : Percent(row.CpuOfResonite),
            Percent(row.WaitShare), row.LiveEntries.ToString("N0", CultureInfo.InvariantCulture), Size(row.LiveBytes)
        }).ToList();
        Table(text, ["Time", "Sent per second", "Journaled per second", "Removed per second", "Bytes per second", "Copied per second", "Threads in hook", "CPU, % of Resonite", "Waiting", "Live entries", "Journal size"], timeline);
        text.AppendLine();
        text.AppendLine("CPU is the CPU time used inside the hook as a share of all of Resonite's CPU time, so it never passes 100%.");
        text.AppendLine("Threads in hook is the time every thread spent inside the hook, added up and divided by the time: 1.00 means one thread was inside it the whole time, busy or waiting.");
        text.AppendLine("Share of hook time, Waiting and Capturing are shares of that time inside the hook.");
        text.AppendLine("Bytes counts every command and its data. Copied counts only what was copied into the journal and archive. Particle, trail and mesh data is not copied when sent: it is read from the engine's memory when a recovery starts, except meshes loaded from files, which are copied once when the engine frees them after loading.");
        text.AppendLine("Average life is how long removed entries stayed in the journal before they were replaced or unloaded.");
        return text.ToString();
    }

    private static string Lifetime(TypeCounters counters)
    {
        long removed = counters.Removed;
        if (removed == 0)
            return "-";

        double seconds = counters.LifetimeTicks / (double)Stopwatch.Frequency / removed;
        return seconds < 1 ? $"{seconds * 1000:F0} ms" : Duration(seconds);
    }

    internal static string Duration(double seconds) => seconds switch
    {
        < 60 => $"{seconds:F1} seconds",
        < 3600 => $"{seconds / 60:F1} minutes",
        _ => $"{seconds / 3600:F1} hours"
    };

    private static string Rate(double count, double seconds)
    {
        double value = count / seconds;
        return value >= 100 ? value.ToString("N0", CultureInfo.InvariantCulture) : value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    internal static string Percent(double value) => value switch
    {
        < 0.01 => "0%",
        < 1 => value.ToString("F2", CultureInfo.InvariantCulture) + "%",
        _ => value.ToString("F1", CultureInfo.InvariantCulture) + "%"
    };

    private static string Size(long bytes) => DashPanel.Bytes(bytes);

    private static void Heading(StringBuilder text, string title)
    {
        text.AppendLine(title.ToUpperInvariant());
        text.AppendLine(new string('=', title.Length));
    }

    internal static void Table(StringBuilder text, string[] headers, List<string[]> rows)
    {
        var widths = new int[headers.Length];
        for (int column = 0; column < headers.Length; column++)
        {
            widths[column] = headers[column].Length;
            foreach (string[] row in rows)
            {
                if (column < headers.Length - 1)
                    widths[column] = Math.Max(widths[column], row[column].Length);
            }
        }
        AppendRow(headers);
        AppendRow(widths.Select((width, column) => new string('-', column == headers.Length - 1 ? headers[column].Length : width)).ToArray());
        foreach (string[] row in rows)
            AppendRow(row);

        void AppendRow(string[] cells)
        {
            var line = new StringBuilder();
            for (int column = 0; column < cells.Length; column++)
            {
                if (column > 0)
                    line.Append("  ");

                line.Append(column == cells.Length - 1 ? cells[column] : cells[column].PadRight(widths[column]));
            }
            text.AppendLine(line.ToString().TrimEnd());
        }
    }
}
