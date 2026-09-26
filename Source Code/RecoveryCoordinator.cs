using System.Diagnostics;
using System.Reflection;
using System.Collections.Concurrent;
using FrooxEngine;
using HarmonyLib;
using Renderite.Shared;

namespace RenderiteRecovery;

internal static class RecoveryCoordinator
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(30);
    private const int QuietPeriodMs = 300;
    private const int MaximumQuiescenceWaitMs = 5_000;

    private static readonly CommandJournal Journal = new();
    private static readonly ReplayPool Pool = new();
    private static readonly AsyncLocal<bool> RecoverySend = new();
    private static readonly HashSet<int> MeshesNeedingFullUpload = new();
    private static volatile bool _anyMeshNeedsFullUpload;
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> ReplayAcks = new();
    private static readonly SemaphoreSlim FrameStarts = new(0);
    private static readonly MethodInfo HandleCommandMethod = AccessTools.Method(typeof(RenderSystem), "HandleCommand");
    private const int HeartbeatIntervalMs = 4_000;

    private static readonly ConcurrentDictionary<int, List<CommandJournal.Entry>> DeferredVideo = new();
    private static readonly ConcurrentDictionary<int, byte> VideoTexturesToRebind = new();
    private static RenderiteMessagingHost? _replayHost;
    private static readonly List<IDisposable> PersistentLeases = new();
    private static int _attemptsUsed;
    private static long _lastRecoveredTick;

    private static long _persistentLeaseBytes;
    private static long _deferredBytes;
    private static long _replayedBytes;
    private static int _replayedCommands;
    private static int _replayTotal;
    private static int _recoveries;
    private static long _recoveryStartedTick;
    private static long _lastRecoveryMs;
    private static long _lastRecoveryBytes;
    private static int _lastRecoveryCommands;
    private static DateTime? _lastRecoveryLocal;
    private static int _gaveUp;

    private static int _deliberateKillPid;
    private static int _journalOffReported;
    private static int _monitorErrors;
    private static readonly TimeSpan GiveUpExitTimeout = TimeSpan.FromMinutes(2);

    private const int MaximumDeferred = 50_000;
    private static readonly object DeferGate = new();
    private static readonly List<DeferredCommand> DeferredCommands = new();
    private static bool _deferOverflow;
    private sealed record DeferredCommand(byte[] Payload, bool Background, IReadOnlyList<PayloadArchiver.Buffer> Buffers);

    private static readonly Queue<string> RecentReplay = new();
    private static int _replaying;
    private static object? _probing;
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(10);
    private static int _resumeRequested;
    private static Task? _watchdog;
    private static readonly TimeSpan BootstrapperTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetireTimeout = TimeSpan.FromSeconds(5);
    private static long _lastHeartbeatTick;
    private static int _started;
    private static int _recovering;
    private static int _suspended;
    private static long _suppressForceCrashUntil;
    private static long _lastQuarantinedTick;
    private static RenderSystem? _renderSystem;
    private static Process? _replacementProcess;
    private static FrameStartData? _latestFrameStart;
    private static CancellationTokenSource? _abort;
    private static string? _abortReason;

    internal static bool IsRecovering => Volatile.Read(ref _recovering) != 0;

    internal sealed record StatusReport(
        string State,
        bool Healthy,
        int Recoveries,
        int AttemptsUsed,
        int JournalEntries,
        long JournalMemoryBytes,
        long ArchiveLiveBytes,
        long ArchiveMemoryBytes,
        long ArchiveDiskBytes,
        int ArchiveSegments,
        int DeferredCommands,
        long DeferredBytes,
        long PersistentLeaseBytes,
        int ReplayedCommands,
        int ReplayTotal,
        long ReplayedBytes,
        long LastRecoveryMs,
        long LastRecoveryBytes,
        int LastRecoveryCommands,
        DateTime? LastRecoveryLocal,
        IReadOnlyList<AssetQuarantine.Key> QuarantinedAssets,
        int SuspectAssets,
        int BlockedCommands);

    internal static Dictionary<(string Family, int AssetId), long>? UploadBytesByAsset() =>
        Journal.CanRecord ? Journal.UploadBytesByAsset() : null;

    internal static CommandJournal.Composition? JournalComposition() => Journal.Describe();

    internal static CommandJournal.Contents JournalContents() => Journal.ListContents();

    internal static StatusReport GetStatus()
    {
        (int entries, long journalBytes) = Journal.Usage;
        int deferredCount;
        long deferredBytes;
        lock (DeferGate)
        {
            deferredCount = DeferredCommands.Count;
            deferredBytes = _deferredBytes;
        }
        (IReadOnlyList<AssetQuarantine.Key> quarantined, int suspects) = AssetQuarantine.Snapshot();
        string state;
        bool healthy = false;
        if (Journal.IncompleteReason is string reason)
            state = "Disabled for this session: " + reason + ".";
        else if (IsRecovering)
            state = $"Recovering: replayed {Volatile.Read(ref _replayedCommands)} of {Volatile.Read(ref _replayTotal)} commands.";
        else if (Volatile.Read(ref _gaveUp) != 0)
            state = Settings.ExitWhenRecoveryFails
                ? "Gave up (recovery_attempts reached). Resonite is exiting."
                : "Gave up (recovery_attempts reached). Rendering is suspended. Restart Resonite.";
        else if (Volatile.Read(ref _suspended) != 0)
            state = "Resuming rendering.";
        else
        {
            state = "Ready. Journal is recording.";
            healthy = true;
        }
        return new StatusReport(state, healthy, Volatile.Read(ref _recoveries), Volatile.Read(ref _attemptsUsed), entries, journalBytes,
            PayloadArchiver.LiveBytes, PayloadArchiver.MemoryBytes, PayloadArchiver.DiskBytes, PayloadArchiver.SegmentCount,
            deferredCount, deferredBytes, Volatile.Read(ref _persistentLeaseBytes),
            Volatile.Read(ref _replayedCommands), Volatile.Read(ref _replayTotal), Volatile.Read(ref _replayedBytes),
            Volatile.Read(ref _lastRecoveryMs), Volatile.Read(ref _lastRecoveryBytes),
            Volatile.Read(ref _lastRecoveryCommands), _lastRecoveryLocal,
            quarantined, suspects, AssetQuarantine.BlockedCommands);
    }
    internal static bool RenderingSuspended => IsRecovering || Volatile.Read(ref _suspended) != 0;
    internal static bool HandlesRendererExit => !ForceExit.Requested && Journal.CanRecord;

    internal static void NoteDeliberateKill(int processId) => Volatile.Write(ref _deliberateKillPid, processId);
    internal static bool ShouldSuppressForceCrash => RenderingSuspended
        || Environment.TickCount64 < Volatile.Read(ref _suppressForceCrashUntil);

    internal static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        _ = Task.Run(MonitorLoop);
    }

    internal static void Observe(RenderSystem renderSystem)
    {
        _renderSystem = renderSystem;
        EnsureInitializationSnapshot(renderSystem);
    }

    internal static void InvalidateJournal(string reason) => Journal.Invalidate(reason);

    internal static void Record(RendererCommand command, bool background)
    {
        if (RenderingSuspended || !Journal.CanRecord || !CommandJournal.ShouldRecord(command))
            return;

        try
        {
            RenderSystem? renderSystem = _renderSystem ?? Engine.Current?.RenderSystem;
            if (renderSystem is not null)
                _renderSystem = renderSystem;

            long captureStart = JournalTelemetry.Now();
            IReadOnlyList<PayloadArchiver.Buffer> buffers = renderSystem is null
                ? Array.Empty<PayloadArchiver.Buffer>() : PayloadArchiver.CaptureForJournal(command, renderSystem);
            JournalTelemetry.Add(JournalTelemetry.Stage.Capture, captureStart);
            Journal.Record(command, background, buffers);
        }
        catch (Exception ex) { Journal.Invalidate($"Could not journal {command.GetType().Name}: {ex.Message}"); }
    }

    internal static bool AllowOutbound => !RenderingSuspended || RecoverySend.Value;

    internal static bool HoldMaterialBatch(RendererCommand command) =>
        command is MaterialsUpdateBatch batch && !RecoverySend.Value && MaterialRefresh.ShouldDrop(batch, _renderSystem);

    internal static bool TryDefer(RendererCommand command, bool background)
    {
        if (RecoverySend.Value || !RenderingSuspended)
            return false;

        lock (DeferGate)
        {
            if (!RenderingSuspended)
                return false;

            if (command is FrameSubmitData or KeepAlive)
                return true;

            if (DeferredCommands.Count >= MaximumDeferred)
            {
                if (!_deferOverflow)
                {
                    _deferOverflow = true;
                    Journal.Invalidate($"more than {MaximumDeferred} engine commands arrived while the renderer was being recovered");
                }
                return true;
            }
            IReadOnlyList<PayloadArchiver.Buffer> buffers = Array.Empty<PayloadArchiver.Buffer>();
            RenderSystem? system = _renderSystem;
            try
            {
                if (system is not null && Journal.CanRecord && CommandJournal.ShouldRecord(command))
                    buffers = PayloadArchiver.Capture(command, system);
            }
            catch (Exception ex) { Journal.Invalidate($"Could not journal {command.GetType().Name}: {ex.Message}"); }
            byte[] payload = CommandJournal.Serialize(command);
            DeferredCommands.Add(new DeferredCommand(payload, background, buffers));
            _deferredBytes += payload.LongLength;
            return true;
        }
    }

    internal static bool BlockQuarantined(RendererCommand command) =>
        !RecoverySend.Value && AssetQuarantine.Blocks(command);

    internal static void DeliverQuarantineReplies(RenderSystem system)
    {
        if (RenderingSuspended)
            return;

        foreach (RendererCommand reply in AssetQuarantine.TakeReplies())
        {
            try { Forward(system, reply, 0); }
            catch (Exception ex) { RenderiteRecoveryMod.Warn($"Could not answer {reply.GetType().Name} for a quarantined asset: {ex.Message}"); }
        }
    }

    internal static void CompleteHeldMaterialBatches(RenderSystem system) =>
        MaterialRefresh.DeliverCompletions(reply => Forward(system, reply, 0));

    internal static void PrepareOutbound(RendererCommand command)
    {
        if (RecoverySend.Value)
            return;

        CompleteSkippedMesh(command);
        InFlightRequests.Sent(command);
        AssetQuarantine.NoteSent(command);
        if (command is not MaterialsUpdateBatch batch || !PropertyIdMap.IsTranslating)
            return;

        RenderSystem? system = _renderSystem;
        if (system is null)
            return;

        try { PropertyIdMap.Rewrite(batch, system); }
        catch (Exception ex) { RenderiteRecoveryMod.Error($"Could not translate material property IDs: {ex}"); }
    }

    internal static bool Request(RenderSystem renderSystem, string reason)
    {
        if (ForceExit.Requested || renderSystem.Engine.ShutdownRequested)
        {
            RenderiteRecoveryMod.Msg($"Renderer stopped while Resonite is exiting ({reason}), so it will not be recovered.");
            return false;
        }
        Observe(renderSystem);
        if (Volatile.Read(ref _suspended) != 0 && Volatile.Read(ref _recovering) == 0)
        {
            RenderiteRecoveryMod.Warn($"Ignoring additional renderer failure while recovery is suspended: {reason}");
            ReleaseFrameWait(renderSystem);
            return true;
        }
        if (IsRecovering)
        {
            RenderiteRecoveryMod.Msg($"Ignoring renderer failure report during recovery: {reason}");
            return true;
        }
        if (Journal.IncompleteReason is string journalOff)
        {
            if (Interlocked.Exchange(ref _journalOffReported, 1) == 0)
                RenderiteRecoveryMod.Error($"The renderer stopped ({reason}), but recovery is disabled for this session ({journalOff}), so Resonite handles it as usual.");

            return false;
        }
        if (Interlocked.CompareExchange(ref _recovering, 1, 0) != 0)
            return true;

        Volatile.Write(ref _suspended, 1);
        Volatile.Write(ref _suppressForceCrashUntil, Environment.TickCount64 + 15_000);
        int attempt = Interlocked.Increment(ref _attemptsUsed);
        int allowed = Settings.RecoveryAttempts;
        if (attempt > allowed)
        {
            Volatile.Write(ref _recovering, 0);
            ReleaseFrameWait(renderSystem);
            GiveUp(renderSystem, $"The renderer failed again ({reason}) less than {RenderiteRecoveryMod.Seconds(Settings.StableSeconds)} after the last recovery (stable_seconds), and all {allowed} attempts (recovery_attempts) are used up.");
            return true;
        }
        RenderiteRecoveryMod.Warn($"Renderer failure detected ({reason}), starting recovery (attempt {attempt} of {allowed}).");
        RendererDiagnostics.ObserveOriginal(EngineStartUtc());
        bool deliberate = IsDeliberateKill(renderSystem.RendererProcess);
        if (!deliberate)
            RendererDiagnostics.ReportStopped(reason);

        AssetQuarantine.RendererDied(InFlightRequests.OutstandingAssets(), deliberate);
        InFlightRequests.Orphan();
        MaterialRefresh.Begin(renderSystem.Engine);
        ReleaseFrameWait(renderSystem);
        _ = Task.Run(() => Recover(renderSystem));
        return true;
    }

    private static bool IsDeliberateKill(Process? process)
    {
        int expected = Interlocked.Exchange(ref _deliberateKillPid, 0);
        if (expected == 0 || process is null)
            return false;

        try { return process.Id == expected; }
        catch (InvalidOperationException) { return false; }
    }

    private static void ResetAttemptsWhenStable()
    {
        if (Volatile.Read(ref _attemptsUsed) == 0
            || Environment.TickCount64 - Volatile.Read(ref _lastRecoveredTick) < Settings.StableSeconds * 1000L)
            return;

        int used = Interlocked.Exchange(ref _attemptsUsed, 0);
        if (used > 0)
            RenderiteRecoveryMod.Msg($"The renderer has run {RenderiteRecoveryMod.Seconds(Settings.StableSeconds)} since the last recovery, so the next failure gets all {Settings.RecoveryAttempts} attempts again ({used} were used).");
    }

    private static void ReplacementFailed(RenderSystem renderSystem, string reason)
    {
        if (!IsRecovering)
        {
            Request(renderSystem, reason);
            return;
        }
        CancellationTokenSource? abort = Volatile.Read(ref _abort);
        if (abort is null || abort.IsCancellationRequested)
            return;

        Interlocked.CompareExchange(ref _abortReason, reason, null);
        RenderiteRecoveryMod.Warn($"Replacement renderer failed during recovery ({reason}), aborting this attempt.");
        RendererDiagnostics.ReportStopped(reason);
        abort.Cancel();
    }

    private static async Task MonitorLoop()
    {
        while (true)
        {
            await Task.Delay(250).ConfigureAwait(false);
            RenderSystem? renderSystem = _renderSystem;
            if (renderSystem?.Engine is null || renderSystem.Engine.ShutdownRequested || ForceExit.Requested)
                continue;

            using var measure = ResourceMetrics.Measure(ResourceMetrics.Area.Background);
            try
            {
                if (RenderingSuspended)
                    SendHeartbeat(renderSystem);

                if (IsRecovering)
                {
                    Process? replacement = Volatile.Read(ref _replacementProcess);
                    if (replacement is not null && replacement.HasExited)
                        ReplacementFailed(renderSystem, $"replacement renderer exited ({DescribeExit(replacement)})");

                    continue;
                }
                if (RenderingSuspended)
                    continue;

                ResetAttemptsWhenStable();
                if (!HandlesRendererExit)
                    continue;

                Process? process = renderSystem.RendererProcess;
                if (process is not null && process.HasExited)
                    Request(renderSystem, "process exited");
            }
            catch (InvalidOperationException) { }
            catch (Exception ex)
            {
                int errors = Interlocked.Increment(ref _monitorErrors);
                if (errors <= 5 || errors % 1000 == 0)
                    RenderiteRecoveryMod.Error($"Renderer monitor error ({errors} so far), still monitoring: {ex}");
            }
        }
    }

    private static void SendHeartbeat(RenderSystem renderSystem)
    {
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _lastHeartbeatTick) < HeartbeatIntervalMs)
            return;

        Volatile.Write(ref _lastHeartbeatTick, now);
        try { renderSystem.Engine.BootstrapperManager?.SendHeartbeat(); }
        catch (Exception ex) { RenderiteRecoveryMod.Warn($"Could not send bootstrapper heartbeat: {ex.Message}"); }
    }

    private static string DescribeExit(Process process)
    {
        try { return $"exit code {process.ExitCode}"; }
        catch (Exception) { return "exit code unavailable"; }
    }

    private static void RetireRenderer(RenderiteMessagingHost host, Process? process)
    {
        bool running;
        try { running = process is not null && !process.HasExited; }
        catch (InvalidOperationException) { running = false; }
        if (!running)
            return;

        try { SendRecoveryCommand(host, new RendererShutdown(), false); }
        catch (Exception ex) { RenderiteRecoveryMod.Warn($"Could not ask the old renderer to close: {ex.Message}"); }
        _ = Task.Run(async () =>
        {
            try { await process!.WaitForExitAsync().WaitAsync(RetireTimeout).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                RenderiteRecoveryMod.Warn($"The old renderer (PID {process!.Id}) did not close, so it is being stopped.");
                KillReplacement(process);
            }
            catch (Exception) { }
        });
    }

    private static void KillReplacement(Process? process)
    {
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch (Exception ex) { RenderiteRecoveryMod.Warn($"Could not stop the failed replacement renderer: {ex.Message}"); }
    }

    private static async Task Recover(RenderSystem renderSystem)
    {
        bool succeeded = false;
        bool confirmedSuspect = false;
        Process? replacement = null;
        var abort = new CancellationTokenSource();
        Volatile.Write(ref _abortReason, null);
        Volatile.Write(ref _abort, abort);
        Volatile.Write(ref _replacementProcess, null);
        Volatile.Write(ref _latestFrameStart, null);
        Volatile.Write(ref _probing, null);
        while (FrameStarts.Wait(0)) { }
        var pipeline = new ReplayPipeline();
        var recentKeys = new Queue<AssetQuarantine.Key>();
        PropertyIdMap.BeginRenderer();
        DeferredVideo.Clear();
        VideoTexturesToRebind.Clear();
        foreach (IDisposable lease in PersistentLeases)
            lease.Dispose();

        PersistentLeases.Clear();
        Volatile.Write(ref _persistentLeaseBytes, 0);
        Volatile.Write(ref _replayedBytes, 0);
        Volatile.Write(ref _replayedCommands, 0);
        Volatile.Write(ref _replayTotal, 0);
        Volatile.Write(ref _gaveUp, 0);
        Volatile.Write(ref _recoveryStartedTick, Environment.TickCount64);
        try
        {
            CommandJournal.Snapshot snapshot = Journal.Capture();
            if (snapshot.Initialization is null)
                throw new InvalidOperationException("RendererInitData was not captured.");

            RenderiteMessagingHost oldHost = GetHost(renderSystem);
            RetireRenderer(oldHost, renderSystem.RendererProcess);
            DisposeHost(oldHost);

            var newHost = new RenderiteMessagingHost(
                (command, size) => InvokeHandleCommand(renderSystem, command, size),
                ex => ReplacementFailed(renderSystem, "replacement transport failure: " + ex.Message));
            SetHost(renderSystem, newHost);
            Volatile.Write(ref _replayHost, newHost);

            Process process = await StartRenderer(renderSystem, newHost).ConfigureAwait(false);
            replacement = process;
            Volatile.Write(ref _replacementProcess, process);
            AccessTools.Property(typeof(RenderSystem), nameof(RenderSystem.RendererProcess))
                .SetValue(renderSystem, process);
            AccessTools.Property(typeof(RenderSystem), nameof(RenderSystem.State))
                .SetValue(renderSystem, RendererState.StartingUp);
            ResetHandshakeState(renderSystem);
            if (!RendererWatchdogPatch.Active && (_watchdog is null || _watchdog.IsCompleted))
                _watchdog = (Task)AccessTools.Method(typeof(RenderSystem), "RendererWatchdog").Invoke(renderSystem, null)!;

            RendererCommand init = CommandJournal.Deserialize(snapshot.Initialization, Pool);
            SendRecoveryCommand(newHost, init, false);
            RenderiteRecoveryMod.Msg($"Replacement Renderite process launched. PID: {process.Id}.");
            await WaitForRendering(renderSystem, process, TimeSpan.FromSeconds(45), abort.Token).ConfigureAwait(false);
            AssetQuarantine.Plan plan = AssetQuarantine.PlanReplay();
            var appliedAtFormat = new HashSet<CommandJournal.Entry>(ReferenceEqualityComparer.Instance);
            List<CommandJournal.Entry> ordered = PlaceTexturePropertiesAtFormat(snapshot.Entries, appliedAtFormat);
            var mainOrder = new List<CommandJournal.Entry>(ordered.Count);
            var probeGroups = plan.Probes.Select(key => (Key: key, Entries: new List<CommandJournal.Entry>())).ToList();
            var probeIndex = probeGroups.Select((group, index) => (group.Key, index)).ToDictionary(pair => pair.Key, pair => pair.index);
            var skippedAssets = new HashSet<AssetQuarantine.Key>();
            int skippedEntries = 0;
            foreach (CommandJournal.Entry entry in ordered)
            {
                AssetQuarantine.Key? key = AssetQuarantine.KeyOf(entry);
                if (key is AssetQuarantine.Key skipped && plan.Skipped.Contains(skipped))
                {
                    skippedAssets.Add(skipped);
                    skippedEntries++;
                }
                else if (key is AssetQuarantine.Key suspect && probeIndex.TryGetValue(suspect, out int index))
                    probeGroups[index].Entries.Add(entry);
                else
                    mainOrder.Add(entry);
            }
            List<CommandJournal.Entry> replayed = mainOrder.Concat(probeGroups.SelectMany(group => group.Entries)).ToList();
            int assetCommands = replayed.Count(entry => entry.Type != nameof(RendererInitFinalizeData));
            Volatile.Write(ref _replayTotal, assetCommands);
            SendRecoveryCommand(newHost, RecoveryScreen.Describe(renderSystem, assetCommands), false);
            SendRecoveryCommand(newHost, new RendererInitFinalizeData(), false);
            await WaitForFrameStart(abort.Token).ConfigureAwait(false);
            SendRecoveryCommand(newHost, new DesktopConfig(), false);

            var clock = Stopwatch.StartNew();
            CommandJournal.Entry? desktopConfig = null;
            CommandJournal.Entry? engineReady = null;
            int sent = 0;
            var taskbar = new RecoveryScreen.TaskbarProgress(newHost, assetCommands);
            RenderiteRecoveryMod.Msg($"Replaying {assetCommands} asset and configuration commands: " + string.Join(", ", replayed.GroupBy(entry => entry.Type).OrderByDescending(group => group.Count()).Take(8).Select(group => $"{group.Key} {group.Count()}")) + ".");
            if (skippedEntries > 0)
                RenderiteRecoveryMod.Warn($"Skipping {skippedEntries} commands of {skippedAssets.Count} quarantined assets: {AssetQuarantine.Describe(skippedAssets)}.");

            List<AssetQuarantine.Key> probed = probeGroups.Where(group => group.Entries.Count > 0).Select(group => group.Key).ToList();
            if (probed.Count > 0)
                RenderiteRecoveryMod.Msg($"{probed.Count} suspect assets are replayed one at a time after the rest: {AssetQuarantine.Describe(probed)}.");

            if (probeGroups.Count > probed.Count)
                RenderiteRecoveryMod.Msg($"{probeGroups.Count - probed.Count} suspect assets have nothing in the journal to replay, so they are cleared.");

            DeferVideoCommands(replayed);
            if (appliedAtFormat.Count > 0)
                RenderiteRecoveryMod.Msg($"Texture filtering settings of {appliedAtFormat.Count} textures are replayed with their format.");

            var formatted = new HashSet<(string Family, int AssetId)>();
            var unformatted = new SortedSet<int>();
            int skippedUploads = 0;
            int staleLiveBuffers = 0;
            int staleMeshes = 0;
            int freedMeshes = 0;
            lock (RecentReplay) RecentReplay.Clear();
            Volatile.Write(ref _replaying, 1);

            async Task ReplayEntry(CommandJournal.Entry entry, bool probe)
            {
                if (entry.Type == nameof(RendererInitFinalizeData))
                    return;

                if (entry.Type == nameof(DesktopConfig))
                {
                    desktopConfig = entry;
                    return;
                }
                if (entry.Type == nameof(RendererEngineReady))
                {
                    engineReady = entry;
                    return;
                }
                if (entry.Type == nameof(SetTaskbarProgress))
                    return;

                if (entry.Type is nameof(VideoTextureStartAudioTrack) or nameof(VideoTextureUpdate))
                    return;

                if (TextureStep(entry) is ((string Family, int AssetId) texture, bool isFormat))
                {
                    if (isFormat)
                        formatted.Add(texture);
                    else if (!formatted.Contains(texture))
                    {
                        skippedUploads++;
                        unformatted.Add(texture.AssetId);
                        taskbar.Report(++sent);
                        return;
                    }
                }
                RendererCommand command;
                IReadOnlyList<IDisposable> leases;
                long payloadBytes = entry.Buffers.Sum(buffer => (long)buffer.Length);
                using (ResourceMetrics.Measure(ResourceMetrics.Area.Recovery))
                {
                    command = CommandJournal.Deserialize(entry.Payload, Pool);
                    try { leases = PayloadArchiver.Rehydrate(command, renderSystem, entry.Buffers); }
                    catch (PayloadArchiver.StalePayloadException stale)
                    {
                        if (command is MeshUploadData staleMesh)
                        {
                            if (stale.Changed)
                                staleMeshes++;
                            else
                                freedMeshes++;

                            lock (MeshesNeedingFullUpload)
                            {
                                MeshesNeedingFullUpload.Add(staleMesh.assetId);
                                _anyMeshNeedsFullUpload = true;
                            }
                        }
                        else
                            staleLiveBuffers++;

                        if (InFlightRequests.RequestKey(command) is string requestKey && AssetQuarantine.Reply(requestKey) is RendererCommand consumed)
                            InFlightRequests.Adopt(consumed);

                        taskbar.Report(++sent);
                        return;
                    }
                    if (command is MeshUploadData mesh)
                        mesh.uploadHint = CompleteMeshHint(mesh.uploadHint);

                    if (appliedAtFormat.Contains(entry))
                        ApplyAtNextFormat(command);

                    if (command is MaterialsUpdateBatch batch)
                        PropertyIdMap.Rewrite(batch, renderSystem);

                    if (command is PointRenderBufferUpload or TrailRenderBufferUpload)
                    {
                        PersistentLeases.AddRange(leases);
                        Interlocked.Add(ref _persistentLeaseBytes, payloadBytes);
                        leases = Array.Empty<IDisposable>();
                    }
                    lock (RecentReplay)
                    {
                        RecentReplay.Enqueue(entry.AssetId is int id ? $"{entry.Type} (asset {id})" : entry.Type);
                        while (RecentReplay.Count > 8)
                            RecentReplay.Dequeue();
                    }
                }
                if (!probe && AssetQuarantine.KeyOf(entry) is AssetQuarantine.Key sentKey)
                {
                    recentKeys.Enqueue(sentKey);
                    while (recentKeys.Count > 64)
                        recentKeys.Dequeue();
                }
                await pipeline.SendAsync(newHost, command, entry.Background, leases,
                    payloadBytes, abort.Token, probe).ConfigureAwait(false);
                if (probe)
                    await pipeline.DrainAsync(abort.Token).ConfigureAwait(false);

                Interlocked.Add(ref _replayedBytes, payloadBytes);
                taskbar.Report(++sent);
                Volatile.Write(ref _replayedCommands, sent);
                if (sent % 500 == 0)
                    RenderiteRecoveryMod.Msg($"Replayed {sent}/{assetCommands} commands ({clock.Elapsed.TotalSeconds:F1} seconds).");
            }

            foreach (CommandJournal.Entry entry in mainOrder)
                await ReplayEntry(entry, false).ConfigureAwait(false);

            await pipeline.DrainAsync(abort.Token).ConfigureAwait(false);
            int passed = 0;
            if (probeGroups.Any(group => group.Entries.Count > 0))
            {
                await ProveAlive(newHost, pipeline, abort.Token).ConfigureAwait(false);
                Volatile.Write(ref _replaying, 0);
                foreach ((AssetQuarantine.Key key, List<CommandJournal.Entry> entries) in probeGroups)
                {
                    if (entries.Count > 0)
                    {
                        Volatile.Write(ref _probing, key);
                        foreach (CommandJournal.Entry entry in entries)
                            await ReplayEntry(entry, true).ConfigureAwait(false);

                        await ProveAlive(newHost, pipeline, abort.Token).ConfigureAwait(false);
                        Volatile.Write(ref _probing, null);
                        passed++;
                    }
                    AssetQuarantine.Passed(key);
                }
                SendRecoveryCommand(newHost, new UnloadTexture2D { assetId = AssetQuarantine.BarrierTextureId }, false);
            }
            Volatile.Write(ref _replaying, 0);
            if (staleLiveBuffers > 0)
                RenderiteRecoveryMod.Msg($"{staleLiveBuffers} particle or trail buffers changed or were freed since their last upload, so they were not replayed. The engine sends them again with its next particle update.");

            if (staleMeshes > 0)
                RenderiteRecoveryMod.Msg($"{staleMeshes} meshes changed since their last upload (the engine was already preparing the next one), so they were not replayed. Their next upload is sent in full.");

            if (freedMeshes > 0)
                RenderiteRecoveryMod.Warn($"{freedMeshes} meshes were no longer in the engine's memory and had no saved copy, so they were not replayed. Their next upload is sent in full. A mesh the engine never uploads again stays invisible until it reloads. Please report this with the log.");

            if (skippedUploads > 0)
                RenderiteRecoveryMod.Warn($"Skipped {skippedUploads} texture uploads for {unformatted.Count} textures that had no format in the journal (assets {string.Join(", ", unformatted.Take(20))}{(unformatted.Count > 20 ? ", ..." : "")}). The renderer rejects data before a format, so these textures stay blank until they are reloaded. Please report this with the log.");

            RenderiteRecoveryMod.Msg($"Asset replay complete in {clock.Elapsed.TotalSeconds:F1} seconds. {PropertyIdMap.TranslatedCount} material property IDs are remapped for this renderer.");

            if (desktopConfig is not null)
                SendRecoveryCommand(newHost, CommandJournal.Deserialize(desktopConfig.Payload, Pool), desktopConfig.Background);

            await WaitForQuiescence(abort.Token).ConfigureAwait(false);
            MaterialRefresh.Schedule(renderSystem);
            taskbar.RestoreEngineState(renderSystem);
            if (engineReady is not null)
                SendRecoveryCommand(newHost, CommandJournal.Deserialize(engineReady.Payload, Pool), engineReady.Background);

            HandOffToEngine(renderSystem);
            AssetQuarantine.RecoverySucceeded(passed);
            RenderiteRecoveryMod.Msg($"Renderite recovery completed. New PID: {process.Id}. Resuming on the next engine frame.");
            Volatile.Write(ref _resumeRequested, 1);
            succeeded = true;
            Interlocked.Increment(ref _recoveries);
            Volatile.Write(ref _lastRecoveryMs, Environment.TickCount64 - Volatile.Read(ref _recoveryStartedTick));
            Volatile.Write(ref _lastRecoveryBytes, Volatile.Read(ref _replayedBytes));
            Volatile.Write(ref _lastRecoveryCommands, sent);
            _lastRecoveryLocal = DateTime.Now;
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && abort.IsCancellationRequested)
                ex = new InvalidOperationException($"Recovery aborted: {Volatile.Read(ref _abortReason)}", ex);

            RenderiteRecoveryMod.Error($"Renderite recovery failed without shutting down the engine: {ex}");
            string failure = Volatile.Read(ref _abortReason) ?? ex.GetBaseException().Message;
            RendererDiagnostics.ReportStopped(failure);
            bool duringReplay = Interlocked.Exchange(ref _replaying, 0) != 0;
            object? probing = Interlocked.Exchange(ref _probing, null);
            if (duringReplay && Volatile.Read(ref _abortReason) is string abortReason)
            {
                string recent;
                lock (RecentReplay) recent = string.Join(", ", RecentReplay);
                RenderiteRecoveryMod.Error($"The replacement renderer stopped during asset replay ({abortReason}). The last commands sent were: {recent}.");
            }
            if (!ForceExit.Requested && !renderSystem.Engine.ShutdownRequested)
            {
                if (probing is AssetQuarantine.Key suspect)
                {
                    AssetQuarantine.Confirm(suspect, failure);
                    confirmedSuspect = true;
                }
                else if (duringReplay)
                    AssetQuarantine.ReplayFailed(recentKeys.Reverse().ToList(), pipeline.InFlightAssets());
            }
            ReleaseFrameWait(renderSystem);
        }
        finally
        {
            pipeline.Dispose();
            Volatile.Write(ref _abort, null);
            Volatile.Write(ref _replacementProcess, null);
            Volatile.Write(ref _recovering, 0);
        }

        if (succeeded)
        {
            Volatile.Write(ref _lastRecoveredTick, Environment.TickCount64);
            return;
        }
        KillReplacement(replacement);
        if (ForceExit.Requested || renderSystem.Engine.ShutdownRequested)
        {
            RenderiteRecoveryMod.Msg("Resonite is exiting, so recovery will not be retried.");
            return;
        }
        if (!TakeRetry(renderSystem, confirmedSuspect))
            return;

        await Task.Delay(2_000).ConfigureAwait(false);
        if (Interlocked.CompareExchange(ref _recovering, 1, 0) == 0)
            _ = Task.Run(() => Recover(renderSystem));
    }

    private static bool TakeRetry(RenderSystem renderSystem, bool confirmedSuspect)
    {
        if (Journal.IncompleteReason is string journalOff)
        {
            GiveUp(renderSystem, $"Giving up: the recovery journal was disabled ({journalOff}), so another attempt cannot succeed.");
            return false;
        }
        int allowed = Settings.RecoveryAttempts;
        if (confirmedSuspect)
        {
            Interlocked.Decrement(ref _attemptsUsed);
            RenderiteRecoveryMod.Warn("This attempt found the asset that crashes the renderer, so it does not count against recovery_attempts.");
        }
        else if (Volatile.Read(ref _attemptsUsed) >= allowed)
        {
            GiveUp(renderSystem, $"Giving up after {allowed} recovery attempts (recovery_attempts) without the renderer running {RenderiteRecoveryMod.Seconds(Settings.StableSeconds)} (stable_seconds).");
            return false;
        }
        int next = Interlocked.Increment(ref _attemptsUsed);
        RenderiteRecoveryMod.Warn($"Retrying recovery (attempt {next} of {allowed}) in 2 seconds.");
        return true;
    }

    private static void GiveUp(RenderSystem renderSystem, string reason)
    {
        Volatile.Write(ref _gaveUp, 1);
        if (!Settings.ExitWhenRecoveryFails)
        {
            RenderiteRecoveryMod.Error(reason + " The engine and session stay alive with rendering suspended and no window (exit_when_recovery_fails is false). End Resonite from Task Manager when you are done.");
            return;
        }
        RenderiteRecoveryMod.Error(reason + " Resonite is exiting normally, as if its window had been closed (exit_when_recovery_fails).");
        Engine engine = renderSystem.Engine;
        try { engine.RequestShutdown(); }
        catch (Exception ex) { RenderiteRecoveryMod.Warn($"Could not start the normal exit: {ex.Message}"); }
        _ = Task.Run(async () =>
        {
            await Task.Delay(GiveUpExitTimeout).ConfigureAwait(false);
            ForceExit.Run(engine, $"Resonite did not finish exiting within {GiveUpExitTimeout.TotalMinutes:F0} minutes after recovery gave up, so it is being terminated.");
        });
    }

    internal static void ResumeOnEngineThread(RenderSystem system)
    {
        if (IsRecovering || Interlocked.Exchange(ref _resumeRequested, 0) == 0)
            return;

        StaleFrameResults.Complete(system);
        RenderStateResync.RequestAll();
        FlushDeferredAndResume(system);
        FlushVideoRebinds(system);
        InFlightRequests.Deliver(reply => Forward(system, reply, 0));
        RenderiteRecoveryMod.Msg("Rendering resumed.");
    }

    private static void FlushDeferredAndResume(RenderSystem system)
    {
        lock (DeferGate)
        {
            RenderiteMessagingHost host = GetHost(system);
            int sent = 0;
            foreach (DeferredCommand deferred in DeferredCommands)
            {
                try
                {
                    RendererCommand command = CommandJournal.Deserialize(deferred.Payload, Pool);
                    if (AssetQuarantine.Blocks(command))
                    {
                        PayloadArchiver.Release(deferred.Buffers);
                        continue;
                    }
                    CompleteSkippedMesh(command);
                    Journal.Record(command, deferred.Background, deferred.Buffers);
                    InFlightRequests.Sent(command);
                    if (command is MaterialsUpdateBatch batch)
                        PropertyIdMap.Rewrite(batch, system);

                    SendRecoveryCommand(host, command, deferred.Background);
                    AssetQuarantine.NoteSent(command);
                    sent++;
                }
                catch (Exception ex)
                {
                    RenderiteRecoveryMod.Warn($"Could not send a command queued during recovery: {ex.Message}");
                }
            }
            if (sent > 0)
                RenderiteRecoveryMod.Msg($"Sent {sent} engine commands that were queued during recovery.");

            DeferredCommands.Clear();
            _deferredBytes = 0;
            _deferOverflow = false;
            Volatile.Write(ref _suspended, 0);
        }
    }

    private static void DeferVideoCommands(IReadOnlyList<CommandJournal.Entry> entries)
    {
        foreach (CommandJournal.Entry entry in entries)
        {
            if (entry.AssetId is not int assetId)
                continue;

            if (entry.Type == nameof(VideoTextureLoad))
                DeferredVideo[assetId] = new List<CommandJournal.Entry>();
            else if (entry.Type is nameof(VideoTextureStartAudioTrack) or nameof(VideoTextureUpdate)
                && DeferredVideo.TryGetValue(assetId, out List<CommandJournal.Entry>? deferred))
                deferred.Add(entry);
        }
    }

    private static bool HandleReplayedVideo(RenderSystem system, RendererCommand command)
    {
        switch (command)
        {
            case VideoTextureReady ready when DeferredVideo.TryRemove(ready.assetId, out List<CommandJournal.Entry>? deferred):
                RenderiteMessagingHost? host = Volatile.Read(ref _replayHost);
                if (host is not null)
                {
                    foreach (CommandJournal.Entry entry in deferred)
                        SendRecoveryCommand(host, CommandJournal.Deserialize(entry.Payload, Pool), entry.Background);
                }

                RebindVideo(system, ready.assetId);
                return true;
            case VideoTextureChanged changed when IsRecovering:
                RebindVideo(system, changed.assetId);
                return true;
        }
        return false;
    }

    private static void RebindVideo(RenderSystem system, int assetId)
    {
        if (RenderingSuspended)
            VideoTexturesToRebind[assetId] = 0;
        else
            Forward(system, new VideoTextureChanged { assetId = assetId }, 0);
    }

    private static void FlushVideoRebinds(RenderSystem system)
    {
        foreach (int assetId in VideoTexturesToRebind.Keys)
        {
            if (!VideoTexturesToRebind.TryRemove(assetId, out _))
                continue;

            try { Forward(system, new VideoTextureChanged { assetId = assetId }, 0); }
            catch (Exception ex) { RenderiteRecoveryMod.Warn($"Could not rebind video texture {assetId}: {ex.Message}"); }
        }
    }

    private static async Task WaitForFrameStart(CancellationToken token)
    {
        if (!await FrameStarts.WaitAsync(ReplyTimeout, token).ConfigureAwait(false))
            throw new TimeoutException("Timed out waiting for the replacement renderer to request a frame.");
    }

    private static async Task WaitForQuiescence(CancellationToken token)
    {
        long deadline = Environment.TickCount64 + MaximumQuiescenceWaitMs;
        while (Environment.TickCount64 - Volatile.Read(ref _lastQuarantinedTick) < QuietPeriodMs)
        {
            if (Environment.TickCount64 >= deadline)
            {
                RenderiteRecoveryMod.Warn("Replacement renderer was still replying to replayed commands, resuming anyway.");
                return;
            }
            await Task.Delay(50, token).ConfigureAwait(false);
        }
    }

    private static async Task ProveAlive(RenderiteMessagingHost host, ReplayPipeline pipeline, CancellationToken token)
    {
        var barrier = new SetTexture2DFormat
        {
            assetId = AssetQuarantine.BarrierTextureId, width = 4, height = 4, mipmapCount = 1,
            format = Renderite.Shared.TextureFormat.RGBA32, profile = ColorProfile.sRGB
        };
        await pipeline.SendAsync(host, barrier, false, Array.Empty<IDisposable>(), 0, token, true).ConfigureAwait(false);
        await pipeline.DrainAsync(token, BarrierTimeout).ConfigureAwait(false);
    }

    private static void HandOffToEngine(RenderSystem system)
    {
        FrameStartData start = Interlocked.Exchange(ref _latestFrameStart, null) ?? throw new InvalidOperationException("No pending frame request to hand to the engine.");
        Forward(system, start, 0);
    }

    internal static void SendRecoveryCommand(
        RenderiteMessagingHost host, RendererCommand command, bool background)
    {
        using var measure = ResourceMetrics.Measure(ResourceMetrics.Area.Recovery);
        bool previous = RecoverySend.Value;
        RecoverySend.Value = true;
        try { host.SendCommand(command, background); }
        finally { RecoverySend.Value = previous; }
    }

    private sealed class ReplayPipeline : IDisposable
    {
        private const int MaximumInFlight = 128;
        private const long MaximumInFlightBytes = 256L * 1024 * 1024;

        private readonly Dictionary<string, Pending> _inFlight = new();
        private readonly List<IDisposable> _retained = new();
        private long _inFlightBytes;

        private sealed record Pending(string Key, Task Completion, IReadOnlyList<IDisposable> Leases, long Bytes,
            AssetQuarantine.Key? Asset);

        internal HashSet<AssetQuarantine.Key> InFlightAssets() =>
            _inFlight.Values.Select(pending => pending.Asset).OfType<AssetQuarantine.Key>().ToHashSet();

        internal async Task SendAsync(RenderiteMessagingHost host, RendererCommand command, bool background,
            IReadOnlyList<IDisposable> leases, long bytes, CancellationToken token, bool probe = false)
        {
            string? key = probe ? ProbeResponseKey(command) : ExpectedResponseKey(command);
            if (command is MaterialsUpdateBatch)
                await DrainAsync(token).ConfigureAwait(false);

            if (key is not null && _inFlight.TryGetValue(key, out Pending? sameKey))
                await SettleAsync(sameKey, token).ConfigureAwait(false);

            while (_inFlight.Count > 0
                && (_inFlight.Count >= MaximumInFlight || _inFlightBytes + bytes > MaximumInFlightBytes))
                await SettleAnyAsync(token).ConfigureAwait(false);

            if (key is null)
            {
                SendRecoveryCommand(host, command, background);
                _retained.AddRange(leases);
                return;
            }
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ReplayAcks[key] = completion;
            var pending = new Pending(key, completion.Task, leases, bytes, AssetQuarantine.KeyOf(command));
            _inFlight[key] = pending;
            _inFlightBytes += bytes;
            SendRecoveryCommand(host, command, background);
            if (command is MaterialPropertyIdRequest)
                await SettleAsync(pending, token).ConfigureAwait(false);
        }

        internal async Task DrainAsync(CancellationToken token, TimeSpan? timeout = null)
        {
            while (_inFlight.Count > 0)
                await SettleAnyAsync(token, timeout ?? ReplyTimeout).ConfigureAwait(false);
        }

        private async Task SettleAsync(Pending pending, CancellationToken token)
        {
            try { await pending.Completion.WaitAsync(ReplyTimeout, token).ConfigureAwait(false); }
            catch (TimeoutException ex)
            {
                throw new TimeoutException($"Timed out waiting for replay acknowledgement {pending.Key}.", ex);
            }
            Release(pending);
        }

        private async Task SettleAnyAsync(CancellationToken token, TimeSpan? timeout = null)
        {
            Task[] waiting = _inFlight.Values.Select(pending => pending.Completion).ToArray();
            try { await Task.WhenAny(waiting).WaitAsync(timeout ?? ReplyTimeout, token).ConfigureAwait(false); }
            catch (TimeoutException ex)
            {
                string keys = string.Join(", ", _inFlight.Keys.Take(10));
                throw new TimeoutException($"Timed out waiting for any of {_inFlight.Count} replay acknowledgements ({keys}).", ex);
            }
            foreach (Pending pending in _inFlight.Values.Where(pending => pending.Completion.IsCompleted).ToList())
                Release(pending);
        }

        private void Release(Pending pending)
        {
            if (!_inFlight.Remove(pending.Key))
                return;

            ReplayAcks.TryRemove(pending.Key, out _);
            _inFlightBytes -= pending.Bytes;
            foreach (IDisposable lease in pending.Leases)
                lease.Dispose();
        }

        public void Dispose()
        {
            foreach (Pending pending in _inFlight.Values.ToList())
                Release(pending);

            foreach (IDisposable lease in _retained)
                lease.Dispose();

            _retained.Clear();
        }
    }

    internal static void MeshBufferFreeing(Mesh mesh)
    {
        if (MeshBufferFreePatch.BufferOf(mesh)?.Data is not SharedMemoryBlockLease { Manager: { } manager } lease)
            return;

        Journal.PreserveLive(nameof(MeshUploadData), ((IRendererAsset)mesh).AssetId, manager.Id, lease.BlockBytesStart,
            lease.BlockBytesSize, lease.RawData);
    }

    private static void CompleteSkippedMesh(RendererCommand command)
    {
        if (!_anyMeshNeedsFullUpload || command is not MeshUploadData mesh)
            return;

        lock (MeshesNeedingFullUpload)
        {
            if (!MeshesNeedingFullUpload.Remove(mesh.assetId))
                return;

            _anyMeshNeedsFullUpload = MeshesNeedingFullUpload.Count > 0;
        }
        mesh.uploadHint = CompleteMeshHint(mesh.uploadHint);
    }

    private static MeshUploadHint CompleteMeshHint(MeshUploadHint hint)
    {
        const MeshUploadHint.Flag everything =
            MeshUploadHint.Flag.VertexLayout | MeshUploadHint.Flag.SubmeshLayout | MeshUploadHint.Flag.Geometry
            | MeshUploadHint.Flag.Positions | MeshUploadHint.Flag.Normals | MeshUploadHint.Flag.Tangents
            | MeshUploadHint.Flag.Colors | MeshUploadHint.Flag.UV0s | MeshUploadHint.Flag.UV1s
            | MeshUploadHint.Flag.UV2s | MeshUploadHint.Flag.UV3s | MeshUploadHint.Flag.UV4s
            | MeshUploadHint.Flag.UV5s | MeshUploadHint.Flag.UV6s | MeshUploadHint.Flag.UV7s
            | MeshUploadHint.Flag.BindPoses | MeshUploadHint.Flag.BoneWeights | MeshUploadHint.Flag.Blendshapes;
        const MeshUploadHint.Flag kept = MeshUploadHint.Flag.Dynamic | MeshUploadHint.Flag.Readable;
        return new MeshUploadHint((hint.Flags & kept) | everything);
    }

    private static List<CommandJournal.Entry> PlaceTexturePropertiesAtFormat(
        IReadOnlyList<CommandJournal.Entry> entries, HashSet<CommandJournal.Entry> moved)
    {
        var lastFormat = new Dictionary<(string Family, int AssetId), int>();
        for (int i = 0; i < entries.Count; i++)
        {
            if (TextureStep(entries[i]) is ((string Family, int AssetId) texture, true))
                lastFormat[texture] = i;
        }

        var beforeFormat = new Dictionary<int, List<CommandJournal.Entry>>();
        foreach (CommandJournal.Entry entry in entries)
        {
            if (TextureProperties(entry) is not (string Family, int AssetId) texture
                || !lastFormat.TryGetValue(texture, out int format))
                continue;

            if (!beforeFormat.TryGetValue(format, out List<CommandJournal.Entry>? list))
                beforeFormat[format] = list = new List<CommandJournal.Entry>();

            list.Add(entry);
            moved.Add(entry);
        }
        var ordered = new List<CommandJournal.Entry>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            if (beforeFormat.TryGetValue(i, out List<CommandJournal.Entry>? settings))
                ordered.AddRange(settings);

            if (!moved.Contains(entries[i]))
                ordered.Add(entries[i]);
        }
        return ordered;
    }

    private static (string Family, int AssetId)? TextureProperties(CommandJournal.Entry entry)
    {
        if (entry.AssetId is not int id)
            return null;

        return entry.Type switch
        {
            nameof(SetTexture2DProperties) => ("Texture2D", id),
            nameof(SetTexture3DProperties) => ("Texture3D", id),
            nameof(SetCubemapProperties) => ("Cubemap", id),
            _ => null
        };
    }

    private static void ApplyAtNextFormat(RendererCommand command)
    {
        switch (command)
        {
            case SetTexture2DProperties value: value.applyImmediatelly = false; break;
            case SetTexture3DProperties value: value.applyImmediatelly = false; break;
            case SetCubemapProperties value: value.applyImmediatelly = false; break;
        }
    }

    private static ((string Family, int AssetId) Texture, bool IsFormat)? TextureStep(CommandJournal.Entry entry)
    {
        if (entry.AssetId is not int id)
            return null;

        return entry.Type switch
        {
            nameof(SetTexture2DFormat) => (("Texture2D", id), true),
            nameof(SetTexture2DData) => (("Texture2D", id), false),
            nameof(SetTexture3DFormat) => (("Texture3D", id), true),
            nameof(SetTexture3DData) => (("Texture3D", id), false),
            nameof(SetCubemapFormat) => (("Cubemap", id), true),
            nameof(SetCubemapData) => (("Cubemap", id), false),
            _ => null
        };
    }

    private static DateTime? EngineStartUtc()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return self.StartTime.ToUniversalTime();
        }
        catch (Exception) { return null; }
    }

    private static string? ExpectedResponseKey(RendererCommand command) => command switch
    {
        MeshUploadData value => AckKey(nameof(MeshUploadResult), value.assetId),
        ShaderUpload value => AckKey(nameof(ShaderUploadResult), value.assetId),
        SetTexture2DData value => TextureAckKey(nameof(SetTexture2DResult), value.assetId, TextureUpdateResultType.DataUpload),
        SetTexture3DData value => TextureAckKey(nameof(SetTexture3DResult), value.assetId, TextureUpdateResultType.DataUpload),
        SetCubemapData value => TextureAckKey(nameof(SetCubemapResult), value.assetId, TextureUpdateResultType.DataUpload),
        SetRenderTextureFormat value => AckKey(nameof(RenderTextureResult), value.assetId),
        MaterialsUpdateBatch value => AckKey(nameof(MaterialsUpdateBatchResult), value.updateBatchId),
        MaterialPropertyIdRequest value => AckKey(nameof(MaterialPropertyIdResult), value.requestId),
        _ => null
    };

    private static string? ProbeResponseKey(RendererCommand command) => command switch
    {
        SetTexture2DFormat value => TextureAckKey(nameof(SetTexture2DResult), value.assetId, TextureUpdateResultType.FormatSet),
        SetTexture3DFormat value => TextureAckKey(nameof(SetTexture3DResult), value.assetId, TextureUpdateResultType.FormatSet),
        SetCubemapFormat value => TextureAckKey(nameof(SetCubemapResult), value.assetId, TextureUpdateResultType.FormatSet),
        SetTexture2DProperties { applyImmediatelly: true } value => TextureAckKey(nameof(SetTexture2DResult), value.assetId, TextureUpdateResultType.PropertiesSet),
        SetTexture3DProperties { applyImmediatelly: true } value => TextureAckKey(nameof(SetTexture3DResult), value.assetId, TextureUpdateResultType.PropertiesSet),
        SetCubemapProperties { applyImmediatelly: true } value => TextureAckKey(nameof(SetCubemapResult), value.assetId, TextureUpdateResultType.PropertiesSet),
        PointRenderBufferUpload value => AckKey(nameof(PointRenderBufferConsumed), value.assetId),
        TrailRenderBufferUpload value => AckKey(nameof(TrailRenderBufferConsumed), value.assetId),
        GaussianSplatUpload value => AckKey(nameof(GaussianSplatResult), value.assetId),
        _ => ExpectedResponseKey(command)
    };

    private static string AckKey(string responseType, int id) => $"{responseType}:{id}";

    private static string TextureAckKey(string responseType, int id, TextureUpdateResultType type) => $"{responseType}:{id}:{type}";

    private static void NotifyReplayResponse(RendererCommand command)
    {
        string? key = command switch
        {
            MeshUploadResult value => AckKey(nameof(MeshUploadResult), value.assetId),
            ShaderUploadResult value => AckKey(nameof(ShaderUploadResult), value.assetId),
            SetTexture2DResult value => TextureAckKey(nameof(SetTexture2DResult), value.assetId, value.type),
            SetTexture3DResult value => TextureAckKey(nameof(SetTexture3DResult), value.assetId, value.type),
            SetCubemapResult value => TextureAckKey(nameof(SetCubemapResult), value.assetId, value.type),
            RenderTextureResult value => AckKey(nameof(RenderTextureResult), value.assetId),
            MaterialsUpdateBatchResult value => AckKey(nameof(MaterialsUpdateBatchResult), value.updateBatchId),
            MaterialPropertyIdResult value => AckKey(nameof(MaterialPropertyIdResult), value.requestId),
            PointRenderBufferConsumed value => AckKey(nameof(PointRenderBufferConsumed), value.assetId),
            TrailRenderBufferConsumed value => AckKey(nameof(TrailRenderBufferConsumed), value.assetId),
            GaussianSplatResult value => AckKey(nameof(GaussianSplatResult), value.assetId),
            _ => null
        };
        if (key is not null && ReplayAcks.TryGetValue(key, out var completion))
            completion.TrySetResult(true);
    }

    private static void EnsureInitializationSnapshot(RenderSystem system)
    {
        if (Journal.HasInitialization || system.Engine is null || !system.HasRenderer)
            return;

        try
        {
            var sharedMemory = (SharedMemoryManager)AccessTools.Field(typeof(RenderSystem), "_sharedMemory").GetValue(system)!;
            var outputSource = (TaskCompletionSource<HeadOutputDevice>)AccessTools.Field(typeof(RenderSystem), "_actualHeadOutputDevice").GetValue(system)!;
            HeadOutputDevice output = outputSource.Task.IsCompletedSuccessfully
                ? outputSource.Task.Result : HeadOutputDevice.Autodetect;
            Journal.SetInitialization(new RendererInitData
            {
                sharedMemoryPrefix = sharedMemory.InstancePrefix,
                uniqueSessionId = system.Engine.UniqueSessionID,
                mainProcessId = Environment.ProcessId,
                debugFramePacing = system.DebugFramePacing,
                outputDevice = output,
                windowTitle = "Resonite"
            });
            RenderiteRecoveryMod.Msg("Reconstructed renderer initialization snapshot from the live engine.");
        }
        catch (Exception ex)
        {
            RenderiteRecoveryMod.Warn($"Could not reconstruct RendererInitData yet: {ex.Message}");
        }
    }

    private static async Task<Process> StartRenderer(RenderSystem renderSystem, RenderiteMessagingHost host)
    {
        string rendererPath = renderSystem.RendererPath;
        string? rendererLog = RendererDiagnostics.BeginReplacement(renderSystem.Engine.AppPath,
            Math.Max(1, Volatile.Read(ref _attemptsUsed)));
        string args = $"-QueueName {host.QueueName} -QueueCapacity {host.QueueCapacity} " + RendererDiagnostics.LogArguments(rendererLog);
        if (renderSystem.Engine.BootstrapperManager is not null)
        {
            Task<Process> viaBootstrapper = renderSystem.Engine.BootstrapperManager.StartRenderer(rendererPath, args);
            if (await Task.WhenAny(viaBootstrapper, Task.Delay(BootstrapperTimeout)).ConfigureAwait(false) == viaBootstrapper)
                return await viaBootstrapper.ConfigureAwait(false);

            RenderiteRecoveryMod.Warn($"The bootstrapper did not confirm the renderer start within {BootstrapperTimeout.TotalSeconds:F0} seconds, starting it directly.");
            _ = viaBootstrapper.ContinueWith(late =>
            {
                if (late.IsCompletedSuccessfully)
                    KillReplacement(late.Result);
            }, TaskScheduler.Default);
        }

        string workingDirectory = Path.GetDirectoryName(rendererPath) ?? throw new InvalidOperationException("Renderer path has no parent directory.");
        return Process.Start(new ProcessStartInfo(rendererPath, args)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        }) ?? throw new InvalidOperationException($"Failed to start renderer: {rendererPath}");
    }

    private static async Task WaitForRendering(
        RenderSystem system, Process process, TimeSpan timeout, CancellationToken token)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (system.State != RendererState.Rendering)
        {
            if (process.HasExited)
                throw new InvalidOperationException($"Replacement renderer exited during initialization ({DescribeExit(process)}).");

            if (Environment.TickCount64 >= deadline)
                throw new TimeoutException("Replacement renderer initialization timed out.");

            await Task.Delay(50, token).ConfigureAwait(false);
        }
    }

    private static void InvokeHandleCommand(RenderSystem system, RendererCommand command, int size)
    {
        if (IsRecovering)
        {
            using var measure = ResourceMetrics.Measure(ResourceMetrics.Area.Recovery);
            Quarantine(system, command, size);
            return;
        }
        if (HandleReplayedVideo(system, command))
        {
            if (InFlightRequests.Claim(command))
                Forward(system, command, size);

            return;
        }
        if (command is MaterialPropertyIdResult propertyIds)
            PropertyIdMap.TranslateToEngine(propertyIds);

        Forward(system, command, size);
    }

    private static void Quarantine(RenderSystem system, RendererCommand command, int size)
    {
        try
        {
            switch (command)
            {
                case RendererInitResult:
                    Forward(system, command, size);
                    return;
                case RendererShutdownRequest:
                    if (WindowCloseRequests.Handle(system))
                        Forward(system, command, size);

                    return;
                case FrameStartData start:
                    Volatile.Write(ref _latestFrameStart, start);
                    FrameStarts.Release();
                    return;
                case KeepAlive:
                    return;
            }
            HandleReplayedVideo(system, command);
            if (command is MaterialPropertyIdResult propertyIds)
                PropertyIdMap.RendererAnswered(propertyIds);

            InFlightRequests.Adopt(command);
            NotifyReplayResponse(command);
            Volatile.Write(ref _lastQuarantinedTick, Environment.TickCount64);
        }
        catch (Exception ex)
        {
            RenderiteRecoveryMod.Warn($"Ignored {command.GetType().Name} from the replacement renderer: {ex}");
        }
    }

    private static void Forward(RenderSystem system, RendererCommand command, int size)
    {
        try { HandleCommandMethod.Invoke(system, [command, size]); }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
    }

    private static RenderiteMessagingHost GetHost(RenderSystem system) => (RenderiteMessagingHost)AccessTools.Field(typeof(RenderSystem), "_messagingHost").GetValue(system)!;

    private static void SetHost(RenderSystem system, RenderiteMessagingHost host) => AccessTools.Field(typeof(RenderSystem), "_messagingHost").SetValue(system, host);

    private static void DisposeHost(RenderiteMessagingHost host)
    {
        foreach (string fieldName in new[] { "_primary", "_background" })
        {
            if (AccessTools.Field(typeof(RenderiteMessagingHost), fieldName).GetValue(host) is not MessagingManager manager)
                continue;

            manager.FailureHandler = null!;
            manager.CommandHandler = null!;
            manager.Dispose();
        }
    }

    private static void ResetHandshakeState(RenderSystem system)
    {
        var field = AccessTools.Field(typeof(RenderSystem), "_frameStartEvent");
        if (field.GetValue(system) is EventWaitHandle old)
            old.Set();

        field.SetValue(system, new ManualResetEvent(false));
        AccessTools.Field(typeof(RenderSystem), "_actualHeadOutputDevice").SetValue(system, new TaskCompletionSource<HeadOutputDevice>(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    internal static void ReleaseFrameWait(RenderSystem system)
    {
        if (AccessTools.Field(typeof(RenderSystem), "_frameStartEvent").GetValue(system) is EventWaitHandle handle)
            handle.Set();
    }

    private sealed class ReplayPool : IMemoryPackerEntityPool
    {
        public T Borrow<T>() where T : class, IMemoryPackable, new() => new();
        public void Return<T>(T value) where T : class, IMemoryPackable, new() { }
    }
}
