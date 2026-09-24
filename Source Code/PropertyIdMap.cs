using System.Runtime.InteropServices;
using FrooxEngine;
using Renderite.Shared;

namespace RenderiteRecovery;

internal static class PropertyIdMap
{
    private static readonly object Gate = new();
    private static readonly Dictionary<int, string[]> RequestNames = new();
    private static readonly Dictionary<int, string> EngineNames = new();
    private static readonly Dictionary<string, int> EngineIds = new(StringComparer.Ordinal);
    private static Dictionary<string, int>? _rendererIds;
    private static Dictionary<int, int>? _remap;
    private static int _nextSyntheticId = int.MaxValue;

    internal static bool IsTranslating => Volatile.Read(ref _remap) is { Count: > 0 };

    internal static void RequestSent(MaterialPropertyIdRequest request)
    {
        lock (Gate) RequestNames[request.requestId] = request.propertyNames.ToArray();
    }

    internal static void EngineReceived(MaterialPropertyIdResult result)
    {
        lock (Gate)
        {
            if (!RequestNames.TryGetValue(result.requestId, out string[]? names))
                return;

            for (int i = 0; i < Math.Min(names.Length, result.propertyIDs.Count); i++)
            {
                EngineNames[result.propertyIDs[i]] = names[i];
                EngineIds[names[i]] = result.propertyIDs[i];
            }
        }
    }

    internal static void BeginRenderer()
    {
        lock (Gate)
        {
            _rendererIds = new Dictionary<string, int>(StringComparer.Ordinal);
            Volatile.Write(ref _remap, null);
        }
    }

    internal static void RendererAnswered(MaterialPropertyIdResult result)
    {
        lock (Gate)
        {
            if (_rendererIds is null || !RequestNames.TryGetValue(result.requestId, out string[]? names))
                return;

            for (int i = 0; i < Math.Min(names.Length, result.propertyIDs.Count); i++)
                _rendererIds[names[i]] = result.propertyIDs[i];

            PublishLocked();
        }
    }

    internal static void TranslateToEngine(MaterialPropertyIdResult result)
    {
        lock (Gate)
        {
            if (_rendererIds is null || !RequestNames.TryGetValue(result.requestId, out string[]? names))
                return;

            for (int i = 0; i < Math.Min(names.Length, result.propertyIDs.Count); i++)
            {
                int rendererId = result.propertyIDs[i];
                _rendererIds[names[i]] = rendererId;
                if (!EngineIds.TryGetValue(names[i], out int engineId))
                {
                    engineId = EngineNames.ContainsKey(rendererId) ? _nextSyntheticId-- : rendererId;
                    EngineNames[engineId] = names[i];
                    EngineIds[names[i]] = engineId;
                }
                result.propertyIDs[i] = engineId;
            }
            PublishLocked();
        }
    }

    internal static int TranslatedCount
    {
        get { lock (Gate) return Volatile.Read(ref _remap)?.Count ?? 0; }
    }

    private static void PublishLocked()
    {
        var remap = new Dictionary<int, int>();
        foreach ((int engineId, string name) in EngineNames)
        {
            if (_rendererIds!.TryGetValue(name, out int rendererId) && rendererId != engineId)
                remap[engineId] = rendererId;
        }

        Volatile.Write(ref _remap, remap);
    }

    internal static void Rewrite(MaterialsUpdateBatch batch, RenderSystem system)
    {
        Dictionary<int, int>? remap = Volatile.Read(ref _remap);
        if (remap is null || remap.Count == 0)
            return;

        foreach (SharedMemoryBufferDescriptor<MaterialPropertyUpdate> descriptor in batch.materialUpdates)
        {
            if (descriptor.length <= 0)
                continue;

            Span<MaterialPropertyUpdate> updates = MemoryMarshal.Cast<byte, MaterialPropertyUpdate>(
                PayloadArchiver.Access(system, descriptor.bufferId, descriptor.offset, descriptor.length));
            for (int i = 0; i < updates.Length; i++)
            {
                ref MaterialPropertyUpdate update = ref updates[i];
                if (update.updateType == MaterialPropertyUpdateType.UpdateBatchEnd)
                    return;

                if (update.updateType is >= MaterialPropertyUpdateType.SetFloat and <= MaterialPropertyUpdateType.SetTexture
                    && remap.TryGetValue(update.propertyID, out int rendererId))
                    update.propertyID = rendererId;
            }
        }
    }
}
