using System.Collections.Concurrent;
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

    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();
    private readonly Dictionary<(string Type, int AssetId), Entry> _latest = new();
    private readonly Dictionary<(string Family, int AssetId), List<Entry>> _byAsset = new();
    private readonly Dictionary<int, SetTexture2DFormat> _texture2DFormats = new();
    private readonly HashSet<Entry> _superseded = new(ReferenceEqualityComparer.Instance);
    private long _payloadBytes;
    private long _nextSizeReport = FirstSizeReport;
    private byte[]? _initialization;
    private bool _incomplete;
    private string? _incompleteReason;

    internal bool CanRecord { get { lock (_gate) return !_incomplete; } }

    internal string? IncompleteReason { get { lock (_gate) return _incompleteReason; } }

    internal (int Entries, long Bytes) Usage
    {
        get { lock (_gate) return (_entries.Count - _superseded.Count, _payloadBytes); }
    }

    internal void Invalidate(string reason)
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

    internal void Record(RendererCommand command, bool background, IReadOnlyList<PayloadArchiver.Buffer> buffers)
    {
        if (!ShouldRecord(command))
        {
            PayloadArchiver.Release(buffers);
            return;
        }

        byte[] payload = Serialize(command);
        lock (_gate)
        {
            if (_incomplete)
            {
                PayloadArchiver.Release(buffers);
                return;
            }
            if (command is RendererInitData)
            {
                _initialization = payload;
                PayloadArchiver.Release(buffers);
                return;
            }
            if (Unloads(command) is (string family, string[] types, int unloadedId))
            {
                foreach (string type in types)
                    SupersedeLatest(unloadedId, type);

                SupersedeAsset(family, unloadedId, _ => true);
                if (family == Texture2DFamily)
                    _texture2DFormats.Remove(unloadedId);

                PayloadArchiver.Release(buffers);
                return;
            }

            if (CompactTexture2D(command))
            {
                PayloadArchiver.Release(buffers);
                return;
            }
            var entry = new Entry(payload, background, command.GetType().Name, AssetId(command), buffers);
            if (LatestKey(command, entry) is int key)
            {
                if (_latest.TryGetValue((entry.Type, key), out Entry? previous))
                    Supersede(previous);

                _latest[(entry.Type, key)] = entry;
            }
            if (Family(command) is string assetFamily && entry.AssetId is int assetId)
            {
                if (!_byAsset.TryGetValue((assetFamily, assetId), out List<Entry>? history))
                    _byAsset[(assetFamily, assetId)] = history = new List<Entry>();

                history.Add(entry);
            }
            _entries.Add(entry);
            _payloadBytes += entry.Payload.LongLength;
            TrimToLimits();
        }
    }

    internal Dictionary<(string Family, int AssetId), long> UploadBytesByAsset()
    {
        var bytes = new Dictionary<(string Family, int AssetId), long>();
        lock (_gate)
        {
            foreach (Entry entry in _entries)
            {
                if (entry.Superseded || entry.AssetId is not int assetId || entry.Buffers.Count == 0)
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
                _entries.Where(entry => !entry.Superseded).ToArray());
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
        MeshUnload value => ("Mesh", [nameof(MeshUploadData)], value.assetId),
        PointRenderBufferUnload value => ("PointBuffer", [nameof(PointRenderBufferUpload)], value.assetId),
        TrailRenderBufferUnload value => ("TrailBuffer", [nameof(TrailRenderBufferUpload)], value.assetId),
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
        MeshUploadData or VideoTextureLoad or VideoTextureProperties or VideoTextureUpdate
            or VideoTextureStartAudioTrack or PointRenderBufferUpload or TrailRenderBufferUpload
            or SetTexture2DProperties or SetTexture3DProperties or SetCubemapProperties => entry.AssetId,
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
                        return true;

                    SupersedeAsset(Texture2DFamily, format.assetId, entry =>
                        entry.Type is nameof(SetTexture2DFormat) or nameof(SetTexture2DData));
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
                SupersedeAsset(Texture2DFamily, data.assetId, entry => entry.Type == nameof(SetTexture2DData));
                break;
        }
        return false;
    }

    private void SupersedeLatest(int assetId, string type)
    {
        if (_latest.Remove((type, assetId), out Entry? entry))
            Supersede(entry);
    }

    private void SupersedeAsset(string family, int assetId, Func<Entry, bool> predicate)
    {
        if (!_byAsset.TryGetValue((family, assetId), out List<Entry>? history))
            return;

        foreach (Entry entry in history.Where(predicate).ToList())
        {
            Supersede(entry);
            history.Remove(entry);
        }
        if (history.Count == 0)
            _byAsset.Remove((family, assetId));
    }

    private void Supersede(Entry entry)
    {
        if (entry.Superseded)
            return;

        entry.Superseded = true;
        _superseded.Add(entry);
        _payloadBytes -= entry.Payload.LongLength;
        PayloadArchiver.Release(entry.Buffers);
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
            PayloadArchiver.Release(entry.Buffers);
        }
        _entries.Clear();
        _latest.Clear();
        _byAsset.Clear();
        _texture2DFormats.Clear();
        _superseded.Clear();
        _payloadBytes = 0;
    }

    private void TrimToLimits()
    {
        int liveEntries = _entries.Count - _superseded.Count;
        long bytes = _payloadBytes + PayloadArchiver.LiveBytes;
        if (bytes >= _nextSizeReport)
        {
            RenderiteRecoveryMod.Msg($"Recovery journal is {bytes / (1024 * 1024)} MB live ({liveEntries} entries, {PayloadArchiver.DiskBytes / (1024 * 1024)} MB on disk).");
            while (_nextSizeReport <= bytes)
                _nextSizeReport *= 2;
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
        internal bool Superseded { get; set; }
    }

    internal sealed record Snapshot(byte[]? Initialization, IReadOnlyList<Entry> Entries);
}
