using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Renderite.Shared;
using HarmonyLib;

namespace RenderiteRecovery;

internal sealed class CommandJournal
{
    private const int InitialBufferSize = 64 * 1024;
    private const int MaximumCommandSize = 64 * 1024 * 1024;
    internal static long ByteLimit = 0;
    internal static int EntryLimit = 0;
    private const long FirstSizeReport = 512L * 1024 * 1024;

    private readonly ConcurrentDictionary<(string Type, int AssetId), StreamSlot> _streaming = new();
    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();
    private readonly Dictionary<(string Type, int AssetId), Entry> _latest = new();
    private readonly Dictionary<(string Family, int AssetId), List<Entry>> _byAsset = new();
    private readonly Dictionary<int, SetTexture2DFormat> _texture2DFormats = new();
    private readonly HashSet<Entry> _superseded = new(ReferenceEqualityComparer.Instance);
    private readonly Totals _totals = new();
    private byte[]? _initialization;
    private volatile bool _incomplete;
    private string? _incompleteReason;

    internal bool CanRecord { get { lock (_gate) return !_incomplete; } }

    internal string? IncompleteReason { get { lock (_gate) return _incompleteReason; } }

    internal (int Entries, long Bytes) Usage
    {
        get
        {
            lock (_gate)
            {
                (int count, long bytes) = StreamingTotals();
                return (_entries.Count - _superseded.Count + count, _totals.PayloadBytes + bytes);
            }
        }
    }

    internal void Invalidate(string reason)
    {
        try
        {
            lock (_gate)
            {
                if (_incomplete)
                    return;

                _incomplete = true;
                _incompleteReason = reason;
                ClearEntries();
                RenderiteRecoveryMod.Error("Recovery archive is incomplete: " + reason);
            }
        }
        finally { ReleaseDeferred(); }
    }

    internal void Record(RendererCommand command, bool background, IReadOnlyList<PayloadArchiver.Buffer> buffers)
    {
        if (!ShouldRecord(command))
        {
            PayloadArchiver.Release(buffers);
            return;
        }

        long encodeStart = JournalTelemetry.Now();
        byte[] payload = Serialize(command);
        long waitStart = JournalTelemetry.Add(JournalTelemetry.Stage.Encode, encodeStart);
        if (Streams(command) is (string type, int assetId, bool unload))
        {
            RecordStreaming(background, buffers, payload, type, assetId, unload);
            return;
        }
        try
        {
            lock (_gate)
            {
                long heldStart = JournalTelemetry.Add(JournalTelemetry.Stage.JournalWait, waitStart);
                try { RecordLocked(command, background, buffers, payload); }
                finally { JournalTelemetry.Add(JournalTelemetry.Stage.JournalHeld, heldStart); }
            }
        }
        finally { ReleaseDeferred(); }
    }

    private static (string Type, int AssetId, bool Unload)? Streams(RendererCommand command) => command switch
    {
        PointRenderBufferUpload value => (nameof(PointRenderBufferUpload), value.assetId, false),
        TrailRenderBufferUpload value => (nameof(TrailRenderBufferUpload), value.assetId, false),
        MeshUploadData value => (nameof(MeshUploadData), value.assetId, false),
        PointRenderBufferUnload value => (nameof(PointRenderBufferUpload), value.assetId, true),
        TrailRenderBufferUnload value => (nameof(TrailRenderBufferUpload), value.assetId, true),
        MeshUnload value => (nameof(MeshUploadData), value.assetId, true),
        _ => null
    };

    private void RecordStreaming(bool background, IReadOnlyList<PayloadArchiver.Buffer> buffers, byte[] payload,
        string type, int assetId, bool unload)
    {
        if (_incomplete)
        {
            PayloadArchiver.Release(buffers);
            return;
        }
        var key = (type, assetId);
        if (unload)
        {
            JournalTelemetry.StreamUnloaded(type, assetId);
            if (_streaming.TryGetValue(key, out StreamSlot? unloadedSlot) && Interlocked.Exchange(ref unloadedSlot.Latest, null) is Entry unloaded)
                RemoveStreaming(unloaded, JournalTelemetry.Removal.Unloaded);

            PayloadArchiver.Release(buffers);
            return;
        }
        var entry = new Entry(payload, background, type, assetId, buffers);
        StreamSlot slot = _streaming.GetOrAdd(key, static _ => new StreamSlot());
        if (Interlocked.Exchange(ref slot.Latest, entry) is Entry previous)
            RemoveStreaming(previous, JournalTelemetry.Removal.Replaced);

        JournalTelemetry.Journaled(entry);
        if (_incomplete && Interlocked.CompareExchange(ref slot.Latest, null, entry) == entry)
            RemoveStreaming(entry, null);
    }

    private sealed class StreamSlot
    {
        internal Entry? Latest;
    }

    private sealed class Totals
    {
        internal long PayloadBytes;
        internal long NextSizeReport = FirstSizeReport;
    }

    internal bool PreserveLive(string type, int assetId, int bufferId, int offset, int length, ReadOnlySpan<byte> data)
    {
        if (_incomplete || !_streaming.TryGetValue((type, assetId), out StreamSlot? slot) || Volatile.Read(ref slot.Latest) is not Entry current)
            return false;

        if (current.Buffers.Count != 1 || current.Buffers[0] is not { Storage: null } live
            || live.Id != bufferId || live.Offset != offset || live.Length != length || data.Length != length)
            return false;

        if (live.Hash is ulong hash && PayloadHash.Compute(data) != hash)
            return false;

        PayloadArchiver.Buffer saved = PayloadArchiver.Preserve(live, data);
        var preserved = new Entry(current.Payload, current.Background, current.Type, current.AssetId, [saved]);
        if (Interlocked.CompareExchange(ref slot.Latest, preserved, current) != current)
        {
            PayloadArchiver.Release([saved]);
            return false;
        }
        current.Superseded = true;
        return true;
    }

    private void RemoveStreaming(Entry entry, JournalTelemetry.Removal? reason)
    {
        entry.Superseded = true;
        PayloadArchiver.Release(entry.Buffers);
        if (reason is JournalTelemetry.Removal removal)
            JournalTelemetry.Removed(entry, removal);
    }

    private (int Count, long Bytes) StreamingTotals()
    {
        int count = 0;
        long bytes = 0;
        foreach (KeyValuePair<(string Type, int AssetId), StreamSlot> pair in _streaming)
        {
            if (Volatile.Read(ref pair.Value.Latest) is not Entry entry)
                continue;

            count++;
            bytes += entry.Payload.LongLength;
        }
        return (count, bytes);
    }

    private IEnumerable<Entry> LiveEntries()
    {
        foreach (Entry entry in _entries)
        {
            if (!entry.Superseded)
                yield return entry;
        }
        foreach (KeyValuePair<(string Type, int AssetId), StreamSlot> pair in _streaming)
        {
            if (Volatile.Read(ref pair.Value.Latest) is Entry entry && !entry.Superseded)
                yield return entry;
        }
    }

    [ThreadStatic] private static List<IReadOnlyList<PayloadArchiver.Buffer>>? _deferredReleases;

    private static void DeferRelease(IReadOnlyList<PayloadArchiver.Buffer> buffers)
    {
        if (buffers.Count > 0)
            (_deferredReleases ??= new()).Add(buffers);
    }

    private static void ReleaseDeferred()
    {
        List<IReadOnlyList<PayloadArchiver.Buffer>>? pending = _deferredReleases;
        if (pending is null || pending.Count == 0)
            return;

        try
        {
            foreach (IReadOnlyList<PayloadArchiver.Buffer> buffers in pending)
                PayloadArchiver.Release(buffers);
        }
        finally { pending.Clear(); }
    }

    private void RecordLocked(RendererCommand command, bool background, IReadOnlyList<PayloadArchiver.Buffer> buffers, byte[] payload)
    {
        if (_incomplete)
        {
            DeferRelease(buffers);
            return;
        }
        if (command is RendererInitData)
        {
            _initialization = payload;
            DeferRelease(buffers);
            return;
        }
        if (Unloads(command) is (string family, string[] types, int unloadedId))
        {
            JournalTelemetry.Unloaded(family, unloadedId);
            foreach (string type in types)
                SupersedeLatest(unloadedId, type);

            SupersedeAsset(family, unloadedId, _ => true, JournalTelemetry.Removal.Unloaded);
            if (family == Texture2DFamily)
                _texture2DFormats.Remove(unloadedId);

            DeferRelease(buffers);
            return;
        }

        if (CompactTexture2D(command))
        {
            DeferRelease(buffers);
            return;
        }
        var entry = new Entry(payload, background, command.GetType().Name, AssetId(command), buffers);
        if (LatestKey(command, entry) is int key)
        {
            if (_latest.TryGetValue((entry.Type, key), out Entry? previous))
            {
                Supersede(previous, JournalTelemetry.Removal.Replaced);
                if (Family(command) is string previousFamily && _byAsset.TryGetValue((previousFamily, key), out List<Entry>? previousHistory))
                    previousHistory.Remove(previous);
            }

            _latest[(entry.Type, key)] = entry;
        }
        if (Family(command) is string assetFamily && entry.AssetId is int assetId)
        {
            if (!_byAsset.TryGetValue((assetFamily, assetId), out List<Entry>? history))
                _byAsset[(assetFamily, assetId)] = history = new List<Entry>();

            history.Add(entry);
        }
        _entries.Add(entry);
        _totals.PayloadBytes += entry.Payload.LongLength;
        JournalTelemetry.Journaled(entry);
        TrimToLimits();
    }

    internal sealed record TypeShare(string Type, int Entries, long CommandBytes, long PayloadBytes);

    internal sealed record AssetShare(string Family, int AssetId, int Entries, long Bytes, string Types);

    internal sealed record Composition(IReadOnlyList<TypeShare> Types, IReadOnlyList<AssetShare> Assets,
        int LiveEntries, int ListedEntries, int NoAssetEntries, long CommandBytes, long PayloadBytes,
        int LongestHistory, string? LongestHistoryAsset, long InitializationBytes);

    internal sealed record EntryInfo(string Type, int? AssetId, long CommandBytes, long PayloadBytes, long RecordedTimestamp);

    internal sealed record Contents(IReadOnlyList<EntryInfo> Entries, long InitializationBytes, string? OffReason);

    internal Contents ListContents()
    {
        lock (_gate)
        {
            if (_incomplete)
                return new Contents([], 0, _incompleteReason);

            var entries = new List<EntryInfo>(_entries.Count - _superseded.Count);
            foreach (Entry entry in LiveEntries())
                entries.Add(new EntryInfo(entry.Type, entry.AssetId, entry.Payload.LongLength, entry.BufferBytes, entry.RecordedTimestamp));
            return new Contents(entries, _initialization?.LongLength ?? 0, null);
        }
    }

    internal Composition? Describe()
    {
        var types = new Dictionary<string, (int Entries, long CommandBytes, long PayloadBytes)>(StringComparer.Ordinal);
        var assets = new Dictionary<(string Family, int AssetId), (int Entries, long Bytes, HashSet<string> Types)>();
        int live = 0, listed, noAsset = 0, longest = 0;
        long commandBytes = 0, payloadBytes = 0, initialization;
        string? longestAsset = null;
        lock (_gate)
        {
            if (_incomplete)
                return null;

            listed = _entries.Count + StreamingTotals().Count;
            initialization = _initialization?.LongLength ?? 0;
            foreach (Entry entry in LiveEntries())
            {
                live++;
                long command = entry.Payload.LongLength;
                commandBytes += command;
                payloadBytes += entry.BufferBytes;
                (int count, long commands, long payloads) = types.GetValueOrDefault(entry.Type);
                types[entry.Type] = (count + 1, commands + command, payloads + entry.BufferBytes);
                if (entry.AssetId is not int assetId)
                {
                    noAsset++;
                    continue;
                }
                var key = (JournalTelemetry.FamilyOf(entry.Type), assetId);
                if (!assets.TryGetValue(key, out var asset))
                    asset = (0, 0, new HashSet<string>(StringComparer.Ordinal));

                asset.Types.Add(entry.Type);
                assets[key] = (asset.Entries + 1, asset.Bytes + command + entry.BufferBytes, asset.Types);
            }
            foreach (((string family, int assetId), List<Entry> history) in _byAsset)
            {
                if (history.Count <= longest)
                    continue;

                longest = history.Count;
                longestAsset = $"{family} {assetId}";
            }
        }
        return new Composition(
            types.Select(pair => new TypeShare(pair.Key, pair.Value.Entries, pair.Value.CommandBytes, pair.Value.PayloadBytes))
                .OrderByDescending(share => share.CommandBytes + share.PayloadBytes).ToList(),
            assets.Select(pair => new AssetShare(pair.Key.Item1, pair.Key.Item2, pair.Value.Entries, pair.Value.Bytes,
                    string.Join(", ", pair.Value.Types.Order(StringComparer.Ordinal))))
                .OrderByDescending(share => share.Bytes).ToList(),
            live, listed, noAsset, commandBytes, payloadBytes, longest, longestAsset, initialization);
    }

    internal Dictionary<(string Family, int AssetId), long> UploadBytesByAsset()
    {
        var bytes = new Dictionary<(string Family, int AssetId), long>();
        lock (_gate)
        {
            foreach (Entry entry in LiveEntries())
            {
                if (entry.AssetId is not int assetId || entry.Buffers.Count == 0)
                    continue;

                if (UploadFamily(entry.Type) is not string family)
                    continue;

                long length = 0;
                foreach (PayloadArchiver.Buffer buffer in entry.Buffers)
                    length += buffer.Length;

                bytes[(family, assetId)] = bytes.GetValueOrDefault((family, assetId)) + length;
            }
        }
        return bytes;
    }

    private static string? UploadFamily(string type) => type switch
    {
        nameof(MeshUploadData) => "Mesh",
        nameof(SetTexture3DData) => "Texture3D",
        nameof(SetCubemapData) => "Cubemap",
        nameof(PointRenderBufferUpload) => "PointBuffer",
        nameof(TrailRenderBufferUpload) => "TrailBuffer",
        nameof(GaussianSplatUploadRaw) or nameof(GaussianSplatUploadEncoded) => "GaussianSplat",
        _ => null
    };

    internal bool HasInitialization
    {
        get { lock (_gate) return _initialization is not null; }
    }

    internal void SetInitialization(RendererInitData initialization)
    {
        byte[] payload = Serialize(initialization);
        lock (_gate) _initialization = payload;
    }

    internal Snapshot Capture()
    {
        lock (_gate)
        {
            if (_incomplete)
                throw new InvalidOperationException("Recovery journal is incomplete (see the earlier archive error), so the missing scene history cannot be replayed.");

            return new Snapshot(
                _initialization is null ? null : (byte[])_initialization.Clone(),
                LiveEntries().ToArray());
        }
    }

    internal static bool ShouldRecord(RendererCommand command) => command switch
    {
        RendererInitResult or RendererInitProgressUpdate or FrameStartData
            or RendererShutdownRequest or RendererShutdown or KeepAlive
            or FreeSharedMemoryView => false,
        FrameSubmitData or LightsBufferRendererSubmission => false,
        MaterialsUpdateBatch or UnloadMaterial or UnloadMaterialPropertyBlock => false,
        _ => true
    };

    private const string Texture2DFamily = "Texture2D";

    private static string? Family(RendererCommand command) => command switch
    {
        SetTexture2DFormat or SetTexture2DProperties or SetTexture2DData => Texture2DFamily,
        SetTexture3DFormat or SetTexture3DProperties or SetTexture3DData => "Texture3D",
        SetCubemapFormat or SetCubemapProperties or SetCubemapData => "Cubemap",
        ShaderUpload => "Shader",
        SetRenderTextureFormat => "RenderTexture",
        GaussianSplatUpload => "GaussianSplat",
        SetDesktopTextureProperties => "DesktopTexture",
        _ => null
    };

    private static (string Family, string[] LatestTypes, int AssetId)? Unloads(RendererCommand command) => command switch
    {
        UnloadVideoTexture value => ("Video", [nameof(VideoTextureLoad), nameof(VideoTextureProperties),
            nameof(VideoTextureUpdate), nameof(VideoTextureStartAudioTrack)], value.assetId),
        UnloadTexture2D value => (Texture2DFamily, [nameof(SetTexture2DProperties)], value.assetId),
        UnloadTexture3D value => ("Texture3D", [nameof(SetTexture3DProperties)], value.assetId),
        UnloadCubemap value => ("Cubemap", [nameof(SetCubemapProperties)], value.assetId),
        ShaderUnload value => ("Shader", [], value.assetId),
        UnloadRenderTexture value => ("RenderTexture", [], value.assetId),
        UnloadGaussianSplat value => ("GaussianSplat", [], value.assetId),
        UnloadDesktopTexture value => ("DesktopTexture", [], value.assetId),
        _ => null
    };

    private static int? LatestKey(RendererCommand command, Entry entry) => command switch
    {
        VideoTextureLoad or VideoTextureProperties or VideoTextureUpdate
            or VideoTextureStartAudioTrack or SetTexture2DProperties or SetTexture3DProperties or SetCubemapProperties => entry.AssetId,
        _ => null
    };

    private bool CompactTexture2D(RendererCommand command)
    {
        switch (command)
        {
            case SetTexture2DFormat format:
                if (_texture2DFormats.TryGetValue(format.assetId, out SetTexture2DFormat? previous))
                {
                    bool same = previous.width == format.width && previous.height == format.height
                        && previous.mipmapCount == format.mipmapCount && previous.format == format.format
                        && previous.profile == format.profile;
                    if (same)
                    {
                        JournalTelemetry.Identical(nameof(SetTexture2DFormat), format.assetId);
                        return true;
                    }

                    SupersedeAsset(Texture2DFamily, format.assetId, entry =>
                        entry.Type is nameof(SetTexture2DFormat) or nameof(SetTexture2DData), JournalTelemetry.Removal.Rewritten);
                }
                _texture2DFormats[format.assetId] = new SetTexture2DFormat
                {
                    assetId = format.assetId, width = format.width, height = format.height,
                    mipmapCount = format.mipmapCount, format = format.format, profile = format.profile
                };
                break;
            case SetTexture2DData data when data.startMipLevel == 0 && !data.hint.hasRegion
                && _texture2DFormats.TryGetValue(data.assetId, out SetTexture2DFormat? current)
                && (data.mipMapSizes?.Count ?? 0) >= current.mipmapCount:
                SupersedeAsset(Texture2DFamily, data.assetId, entry => entry.Type == nameof(SetTexture2DData), JournalTelemetry.Removal.Rewritten);
                break;
        }
        return false;
    }

    private void SupersedeLatest(int assetId, string type)
    {
        if (_latest.Remove((type, assetId), out Entry? entry))
            Supersede(entry, JournalTelemetry.Removal.Unloaded);
    }

    private void SupersedeAsset(string family, int assetId, Func<Entry, bool> predicate, JournalTelemetry.Removal reason)
    {
        if (!_byAsset.TryGetValue((family, assetId), out List<Entry>? history))
            return;

        history.RemoveAll(entry =>
        {
            if (!predicate(entry))
                return false;

            Supersede(entry, reason);
            return true;
        });
        if (history.Count == 0)
            _byAsset.Remove((family, assetId));
    }

    private void Supersede(Entry entry, JournalTelemetry.Removal reason)
    {
        if (entry.Superseded)
            return;

        entry.Superseded = true;
        _superseded.Add(entry);
        _totals.PayloadBytes -= entry.Payload.LongLength;
        DeferRelease(entry.Buffers);
        JournalTelemetry.Removed(entry, reason);
        if (_superseded.Count < 1024 || _superseded.Count < _entries.Count / 2)
            return;

        _entries.RemoveAll(_superseded.Contains);
        _superseded.Clear();
    }

    private void ClearEntries()
    {
        foreach (Entry entry in _entries)
        {
            if (entry.Superseded)
                continue;

            entry.Superseded = true;
            DeferRelease(entry.Buffers);
        }
        _entries.Clear();
        foreach (KeyValuePair<(string Type, int AssetId), StreamSlot> pair in _streaming)
        {
            if (Interlocked.Exchange(ref pair.Value.Latest, null) is Entry entry)
                RemoveStreaming(entry, null);
        }
        _latest.Clear();
        _byAsset.Clear();
        _texture2DFormats.Clear();
        _superseded.Clear();
        _totals.PayloadBytes = 0;
    }

    private void TrimToLimits()
    {
        int liveEntries = _entries.Count - _superseded.Count;
        long bytes = _totals.PayloadBytes + PayloadArchiver.LiveBytes;
        if (EntryLimit > 0 || ByteLimit > 0)
        {
            (int streamingCount, long streamingBytes) = StreamingTotals();
            liveEntries += streamingCount;
            bytes += streamingBytes;
        }
        if (bytes >= _totals.NextSizeReport)
        {
            RenderiteRecoveryMod.Msg($"Recovery journal is {bytes / (1024 * 1024)} MB live ({liveEntries} entries, {PayloadArchiver.DiskBytes / (1024 * 1024)} MB on disk).");
            while (_totals.NextSizeReport <= bytes)
                _totals.NextSizeReport *= 2;
        }
        if ((EntryLimit <= 0 || liveEntries <= EntryLimit) && (ByteLimit <= 0 || bytes <= ByteLimit))
            return;

        _incomplete = true;
        _incompleteReason = $"the journal reached {bytes / (1024 * 1024)} MB / {liveEntries} entries (journal_limit_mb / journal_entry_limit)";
        RenderiteRecoveryMod.Error($"Recovery disabled for this session: {_incompleteReason}. Normal rendering continues.");
        ClearEntries();
    }

    private static readonly ConcurrentDictionary<Type, FieldInfo?> AssetIdFields = new();

    internal static int? AssetId(RendererCommand command)
    {
        if (command is AssetCommand asset)
            return asset.assetId;

        FieldInfo? field = AssetIdFields.GetOrAdd(command.GetType(), static type =>
            AccessTools.Field(type, "assetId") is { } found && found.FieldType == typeof(int) ? found : null);
        return field is null ? null : (int)field.GetValue(command)!;
    }

    [ThreadStatic] private static byte[]? _scratch;
    private const int MaximumRetainedScratch = 4 * 1024 * 1024;
    private const int PackerOverrunMargin = 1024;
    private static int _largeCommandsReported;

    internal static byte[] Serialize(RendererCommand command)
    {
        byte[] buffer = _scratch ?? new byte[InitialBufferSize + PackerOverrunMargin];
        while (true)
        {
            Span<byte> writable = buffer.AsSpan(0, buffer.Length - PackerOverrunMargin);
            try
            {
                var packer = new MemoryPacker(writable);
                command.Encode(ref packer);
                int length = packer.ComputeLength(writable);
                byte[] payload = buffer.AsSpan(0, length).ToArray();
                if (buffer.Length <= MaximumRetainedScratch + PackerOverrunMargin)
                    _scratch = buffer;

                return payload;
            }
            catch (Exception) when (writable.Length < MaximumCommandSize)
            {
                int grown = Math.Min(writable.Length * 2, MaximumCommandSize);
                if (Interlocked.Increment(ref _largeCommandsReported) <= 5)
                    RenderiteRecoveryMod.Msg($"Journaling a {command.GetType().Name} larger than {writable.Length / 1024} KB, encoding it again in {grown / 1024} KB.");

                buffer = new byte[grown + PackerOverrunMargin];
            }
        }
    }

    internal static RendererCommand Deserialize(byte[] payload, IMemoryPackerEntityPool pool)
    {
        var unpacker = new MemoryUnpacker(payload.AsSpan(), pool);
        return PolymorphicMemoryPackableEntity<RendererCommand>.Decode(ref unpacker);
    }

    internal sealed class Entry(byte[] payload, bool background, string type, int? assetId,
        IReadOnlyList<PayloadArchiver.Buffer> buffers)
    {
        internal byte[] Payload { get; } = payload;
        internal bool Background { get; } = background;
        internal string Type { get; } = type;
        internal int? AssetId { get; } = assetId;
        internal IReadOnlyList<PayloadArchiver.Buffer> Buffers { get; } = buffers;
        internal long BufferBytes { get; } = SumLengths(buffers);
        internal long ArchivedBytes { get; } = SumArchived(buffers);
        internal long RecordedTimestamp { get; } = Stopwatch.GetTimestamp();
        internal bool Superseded { get; set; }

        private static long SumLengths(IReadOnlyList<PayloadArchiver.Buffer> buffers)
        {
            long total = 0;
            for (int i = 0; i < buffers.Count; i++)
                total += buffers[i].Length;

            return total;
        }

        private static long SumArchived(IReadOnlyList<PayloadArchiver.Buffer> buffers)
        {
            long total = 0;
            for (int i = 0; i < buffers.Count; i++)
            {
                if (buffers[i].Storage is not null)
                    total += buffers[i].Length;
            }
            return total;
        }
    }

    internal sealed record Snapshot(byte[]? Initialization, IReadOnlyList<Entry> Entries);
}
