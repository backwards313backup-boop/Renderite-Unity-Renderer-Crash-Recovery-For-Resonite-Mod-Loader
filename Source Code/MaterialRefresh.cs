using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using FrooxEngine;
using HarmonyLib;
using Renderite.Shared;

namespace RenderiteRecovery;

internal static class MaterialRefresh
{
    [ThreadStatic] private static World? _generatingWorld;
    private static readonly object Gate = new();
    private static readonly HashSet<World> Pending = new(ReferenceEqualityComparer.Instance);
    private static readonly ConcurrentQueue<int> DroppedBatches = new();
    private static int _active;

    internal static void EnterProcessUpdate(World world) => _generatingWorld = world;
    internal static void ExitProcessUpdate() => _generatingWorld = null;

    internal static void Begin(Engine engine)
    {
        lock (Gate)
        {
            foreach (World world in engine.WorldManager.Worlds)
                Pending.Add(world);

            Volatile.Write(ref _active, Pending.Count > 0 ? 1 : 0);
        }
    }

    internal static bool ShouldDrop(MaterialsUpdateBatch batch, RenderSystem? system)
    {
        if (Volatile.Read(ref _active) == 0)
            return false;

        World? world = _generatingWorld;
        lock (Gate)
        {
            if (world is null || !Pending.Contains(world))
                return false;
        }
        try
        {
            if (system is not null && batch.instanceChangedBuffer.length > 0)
            {
                PayloadArchiver.Access(system, batch.instanceChangedBuffer.bufferId,
                    batch.instanceChangedBuffer.offset, batch.instanceChangedBuffer.length).Clear();
            }
        }
        catch (Exception ex)
        {
            RenderiteRecoveryMod.Warn($"Could not clear a held material batch's result bits: {ex.Message}");
        }
        DroppedBatches.Enqueue(batch.updateBatchId);
        return true;
    }

    internal static void DeliverCompletions(Action<RendererCommand> forward)
    {
        while (DroppedBatches.TryDequeue(out int batchId))
        {
            try { forward(new MaterialsUpdateBatchResult { updateBatchId = batchId }); }
            catch (Exception ex) { RenderiteRecoveryMod.Warn($"Could not complete held material batch {batchId}: {ex.Message}"); }
        }
    }

    internal static void Schedule(RenderSystem system)
    {
        List<World> pending;
        lock (Gate) pending = Pending.ToList();
        foreach (World world in pending)
        {
            if (world.IsDestroyed)
            {
                Release(world);
                continue;
            }
            world.RunSynchronously(() =>
            {
                using var measure = ResourceMetrics.Measure(ResourceMetrics.Area.Recovery);
                int refreshed = 0;
                foreach (Component provider in ProvidersIn(system, world))
                {
                    try
                    {
                        if (Refresh(provider))
                            refreshed++;
                    }
                    catch (Exception ex)
                    {
                        RenderiteRecoveryMod.Warn($"Could not resend material {provider}: {(ex.InnerException ?? ex).Message}");
                    }
                }
                Release(world);
                if (refreshed > 0)
                    RenderiteRecoveryMod.Msg($"Resending {refreshed} materials and property blocks in world {world.Name}.");
            });
        }
    }

    private static List<Component> ProvidersIn(RenderSystem system, World world)
    {
        var providers = new List<Component>();
        object materials = system.Materials;
        foreach (string managerField in new[] { "Materials", "PropertyBlocks" })
        {
            object manager = AccessTools.Field(materials.GetType(), managerField).GetValue(materials)!;
            foreach (object asset in LiveAssets(manager))
            {
                if (AccessTools.Property(asset.GetType(), "Owner")?.GetValue(asset) is Component provider && provider.World == world)
                    providers.Add(provider);
            }
        }
        return providers;
    }

    private static void Release(World world)
    {
        lock (Gate)
        {
            Pending.Remove(world);
            if (Pending.Count == 0)
                Volatile.Write(ref _active, 0);
        }
    }

    private static bool Refresh(Component provider)
    {
        Type? baseType = provider.GetType();
        while (baseType is not null && !(baseType.IsGenericType && baseType.GetGenericTypeDefinition() == typeof(MaterialProviderBase<>)))
            baseType = baseType.BaseType;

        if (baseType is null || provider.IsRemoved)
            return false;

        Type assetType = baseType.GetGenericArguments()[0];
        object? asset = AccessTools.Property(baseType, "Asset")?.GetValue(provider);
        if (asset is null)
            return false;

        AccessTools.Method(baseType, "AssetCreated", [assetType]).Invoke(provider, [asset]);
        AccessTools.Method(baseType, "UpdateAsset", [assetType]).Invoke(provider, [asset]);
        return true;
    }

    internal static List<object> LiveAssets(object manager)
    {
        object gate = AccessTools.Field(manager.GetType(), "_lock").GetValue(manager)!;
        var byId = (IDictionary)AccessTools.Field(manager.GetType(), "_assetsById").GetValue(manager)!;
        lock (gate) return byId.Values.Cast<object>().ToList();
    }
}
