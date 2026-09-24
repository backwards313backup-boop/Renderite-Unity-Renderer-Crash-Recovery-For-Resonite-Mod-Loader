using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using FrooxEngine;
using HarmonyLib;
using Renderite.Shared;

namespace RenderiteRecovery;

internal static class RenderStateResync
{
    private static int _generation;
    private static readonly ConditionalWeakTable<RenderManager, StrongBox<int>> Applied = new();

    private static readonly FieldInfo RenderableSlots = AccessTools.Field(typeof(RenderTransformManager), "_renderableSlots");
    private static readonly FieldInfo SlotsToAddOrRemove = AccessTools.Field(typeof(RenderTransformManager), "_renderableSlotsToAddOrRemove");
    private static readonly PropertyInfo TransformAdditions = AccessTools.Property(typeof(RenderTransformManager), "EstimatedAdditionCount");
    private static readonly PropertyInfo RenderTransformIndex = AccessTools.Property(typeof(Slot), "RenderTransformIndex");
    private static readonly PropertyInfo RenderableIndex = AccessTools.Property(typeof(RenderableComponent), "RenderableIndex");
    private static readonly FieldInfo StagedUpdate = AccessTools.Field(typeof(RenderManager), "_stagedRenderSpaceUpdate");
    private static readonly MethodInfo MarkChangesRender = AccessTools.Method(typeof(ReflectionProbe), "MarkChangesRender");

    internal static void RequestAll() => Interlocked.Increment(ref _generation);

    internal static void EnsureCurrent(RenderManager manager)
    {
        int generation = Volatile.Read(ref _generation);
        if (generation == 0)
            return;

        StrongBox<int> applied = Applied.GetValue(manager, _ => new StrongBox<int>(0));
        if (applied.Value == generation)
            return;

        applied.Value = generation;
        if (manager.Transforms is null)
            return;

        try
        {
            StagedUpdate.SetValue(manager, null);
            int transforms = ResetTransforms(manager.Transforms);
            int renderables = 0;
            foreach (PropertyInfo property in typeof(RenderManager).GetProperties())
            {
                object? component = property.GetValue(manager);
                if (component is not null && FindRenderableBase(component.GetType()) is Type baseType)
                    renderables += ResetRenderables(component, baseType);
            }
            RenderiteRecoveryMod.Msg($"Resynchronizing world {manager.World?.Name}: {transforms} transforms, {renderables} renderables.");
        }
        catch (Exception ex)
        {
            RenderiteRecoveryMod.Error($"Could not resynchronize world {manager.World?.Name}: {ex}");
        }
    }

    private static int ResetTransforms(RenderTransformManager transforms)
    {
        var slots = (List<Slot>)RenderableSlots.GetValue(transforms)!;
        var toAdd = (List<Slot>)SlotsToAddOrRemove.GetValue(transforms)!;
        Slot[] allocated = slots.ToArray();
        foreach (Slot slot in allocated)
            RenderTransformIndex.SetValue(slot, -1);

        slots.Clear();
        toAdd.AddRange(allocated);
        TransformAdditions.SetValue(transforms, transforms.EstimatedAdditionCount + allocated.Length);
        return allocated.Length;
    }

    private static int ResetRenderables(object manager, Type baseType)
    {
        var renderables = (IList)AccessTools.Field(baseType, "renderables").GetValue(manager)!;
        MethodInfo cleared = AccessTools.Method(baseType, "RenderableCleared");
        MethodInfo addedOrRemoved = AccessTools.Method(baseType, "RenderableAddedOrRemoved");
        object[] allocated = renderables.Cast<object>().ToArray();
        foreach (object renderable in allocated)
        {
            cleared.Invoke(manager, [renderable]);
            RenderableIndex.SetValue(renderable, -1);
        }
        renderables.Clear();
        foreach (object renderable in allocated)
            addedOrRemoved.Invoke(manager, [renderable]);

        if (manager is ReflectionProbesManager)
            RenderOnChangesProbes(allocated);

        return allocated.Length;
    }

    private static void RenderOnChangesProbes(object[] probes)
    {
        int scheduled = 0;
        foreach (ReflectionProbe probe in probes.OfType<ReflectionProbe>())
        {
            if (probe.ProbeType.Value != ReflectionProbeType.OnChanges)
                continue;

            MarkChangesRender.Invoke(probe, null);
            scheduled++;
        }
        if (scheduled > 0)
            RenderiteRecoveryMod.Msg($"Re-rendering {scheduled} On Changes reflection probes.");
    }

    private static Type? FindRenderableBase(Type? type)
    {
        for (; type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(RenderableComponentManager<,>))
                return type;
        }

        return null;
    }
}
