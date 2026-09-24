using System.Reflection;
using System.Runtime.InteropServices;
using FrooxEngine;
using HarmonyLib;
using Renderite.Shared;

namespace RenderiteRecovery;

internal static class StaleFrameResults
{
    private static readonly FieldInfo ScheduledTasks = AccessTools.Field(typeof(ReflectionProbeSH2Manager), "_scheduledTasks");

    internal static void Complete(RenderSystem system)
    {
        foreach (World world in system.Engine.WorldManager.Worlds)
        {
            try { PostponeUnansweredSH2(world.Render?.ReflectionProbeSH2s, system); }
            catch (Exception ex) { RenderiteRecoveryMod.Warn($"Could not complete stale probe results in {world.Name}: {ex.Message}"); }
        }
    }

    internal static void PostponeUnansweredSH2(ReflectionProbeSH2Manager? manager, RenderSystem system)
    {
        object? lease = manager is null ? null : ScheduledTasks.GetValue(manager);
        if (lease is null)
            return;

        object descriptor = AccessTools.Property(lease.GetType(), "Descriptor").GetValue(lease)!;
        int Read(string name) => (int)AccessTools.Property(descriptor.GetType(), name).GetValue(descriptor)!;
        Span<ReflectionProbeSH2Task> tasks = MemoryMarshal.Cast<byte, ReflectionProbeSH2Task>(PayloadArchiver.Access(system, Read("bufferId"), Read("offset"), Read("length")));
        int postponed = 0;
        for (int i = 0; i < tasks.Length && tasks[i].renderableIndex >= 0; i++)
        {
            if (tasks[i].result != ComputeResult.Scheduled)
                continue;

            tasks[i].result = ComputeResult.Postpone;
            postponed++;
        }
        if (postponed > 0)
            RenderiteRecoveryMod.Msg($"Rescheduling {postponed} ambient light probe computations the renderer never finished.");
    }
}
