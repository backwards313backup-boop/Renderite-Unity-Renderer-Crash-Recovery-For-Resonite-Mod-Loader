using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using FrooxEngine;
using HarmonyLib;
using Renderite.Shared;

namespace RenderiteRecovery;

[HarmonyPatch]
internal static class RendererWatchdogPatch
{
    private static int _reportedPid;

    internal static bool Active { get; private set; }

    private static MethodBase? Target() =>
        AccessTools.AsyncMoveNext(AccessTools.Method(typeof(RenderSystem), "RendererWatchdog"));

    private static bool Prepare()
    {
        if (Target() is not null)
            return true;

        RenderiteRecoveryMod.Warn("Could not find the engine's renderer watchdog loop. It will still try to shut Resonite down when a renderer dies.");
        return false;
    }

    private static MethodBase TargetMethod() => Target()!;

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo hasExited = AccessTools.PropertyGetter(typeof(Process), nameof(Process.HasExited));
        List<CodeInstruction> code = instructions.ToList();
        int calls = code.Count(instruction => instruction.Calls(hasExited));
        if (calls != 1)
        {
            RenderiteRecoveryMod.Warn($"The engine's renderer watchdog changed ({calls} exit checks instead of 1), so it is left unpatched.");
            return code;
        }
        foreach (CodeInstruction instruction in code.Where(instruction => instruction.Calls(hasExited)))
        {
            instruction.opcode = OpCodes.Call;
            instruction.operand = AccessTools.Method(typeof(RendererWatchdogPatch), nameof(HasExited));
        }
        Active = true;
        return code;
    }

    private static bool HasExited(Process process)
    {
        if (!process.HasExited)
            return false;

        if (!RecoveryCoordinator.HandlesRendererExit)
            return true;

        int pid = 0;
        try { pid = process.Id; }
        catch (InvalidOperationException) { }
        if (Interlocked.Exchange(ref _reportedPid, pid) != pid)
            RenderiteRecoveryMod.Msg($"The engine's renderer watchdog saw renderer {pid} exit, recovery handles it, so the watchdog keeps running instead of shutting Resonite down.");

        return false;
    }
}

[HarmonyPatch]
internal static class MeshBufferFreePatch
{
    private static readonly MethodInfo? FreeMeshBuffer = AccessTools.Method(typeof(Mesh), "FreeMeshBuffer");
    private static readonly FieldInfo? MeshBufferField = AccessTools.Field(typeof(Mesh), "meshBuffer");
    private static readonly Lazy<(MethodInfo Method, FieldInfo Owner)?> Found = new(Find);
    private static int _failures;

    internal static bool Active { get; private set; }

    internal static MeshBuffer? BufferOf(Mesh mesh) => MeshBufferField?.GetValue(mesh) as MeshBuffer;

    private static (MethodInfo Method, FieldInfo Owner)? Find()
    {
        if (FreeMeshBuffer is null || MeshBufferField is null)
            return null;

        var callers = new List<MethodInfo>();
        foreach (Type nested in typeof(Mesh).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (MethodInfo method in nested.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                try
                {
                    if (PatchProcessor.ReadMethodBody(method).Any(pair => Equals(pair.Value, FreeMeshBuffer)))
                        callers.Add(method);
                }
                catch (Exception) { }
            }
        }
        if (callers.Count != 1 || callers[0].IsStatic)
            return null;

        FieldInfo? owner = callers[0].DeclaringType!.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(field => field.FieldType == typeof(Mesh));
        return owner is null ? null : (callers[0], owner);
    }

    private static bool Prepare()
    {
        if (Found.Value is not null)
            return true;

        RenderiteRecoveryMod.Warn("Could not find where the engine frees a loaded mesh's shared memory, so mesh data is copied into the journal when it is sent.");
        return false;
    }

    private static MethodBase TargetMethod()
    {
        Active = true;
        return Found.Value!.Value.Method;
    }

    private static void Prefix(object __instance)
    {
        try
        {
            if (Found.Value?.Owner.GetValue(__instance) is Mesh mesh)
                RecoveryCoordinator.MeshBufferFreeing(mesh);
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref _failures) <= 5)
                RenderiteRecoveryMod.Warn($"Could not save a mesh before the engine freed it: {ex.Message}");
        }
    }
}

[HarmonyPatch(typeof(RenderiteMessagingHost), nameof(RenderiteMessagingHost.SendCommand))]
internal static class SendCommandPatch
{
    private static bool Prefix(RendererCommand command, bool isBackground)
    {
        JournalTelemetry.Probe probe = JournalTelemetry.Begin();
        try { return Send(command, isBackground); }
        finally { JournalTelemetry.End(probe, command); }
    }

    private static bool Send(RendererCommand command, bool isBackground)
    {
        using var measure = ResourceMetrics.MeasureCommand(ResourceMetrics.Area.Recording);
        if (RecoveryCoordinator.BlockQuarantined(command))
            return false;

        if (command is MaterialPropertyIdRequest request)
            PropertyIdMap.RequestSent(request);

        if (RecoveryCoordinator.HoldMaterialBatch(command))
            return false;

        if (RecoveryCoordinator.TryDefer(command, isBackground))
            return false;

        RecoveryCoordinator.Record(command, isBackground);
        if (!RecoveryCoordinator.AllowOutbound)
            return false;

        RecoveryCoordinator.PrepareOutbound(command);
        return true;
    }
}

[HarmonyPatch(typeof(RenderSystem), nameof(RenderSystem.WaitForFrameBegin))]
internal static class WaitForFramePatch
{
    private static bool Prefix(RenderSystem __instance, ref FrameStartData? __result)
    {
        using (ResourceMetrics.Measure(ResourceMetrics.Area.EngineHooks))
        {
            ResourceMetrics.CountFrame();
            ResourceMetrics.Tick();
            RecoveryCoordinator.Observe(__instance);
            RecoveryCoordinator.ResumeOnEngineThread(__instance);
            RecoveryCoordinator.DeliverQuarantineReplies(__instance);
            RecoveryCoordinator.CompleteHeldMaterialBatches(__instance);
            TestCrashHotkey.Poll(__instance);
        }
        DashPanel.Poll();
        JournalTelemetry.Poll();
        if (!RecoveryCoordinator.RenderingSuspended)
            return true;

        __result = null;
        Thread.Sleep(16);
        return false;
    }
}

[HarmonyPatch(typeof(ReflectionProbeSH2Manager), "HandleResults")]
internal static class SH2HandleResultsPatch
{
    private static void Prefix(ReflectionProbeSH2Manager __instance) =>
        StaleFrameResults.PostponeUnansweredSH2(__instance, __instance.RenderSystem);
}

[HarmonyPatch(typeof(MaterialUpdateManager), nameof(MaterialUpdateManager.ProcessUpdate))]
internal static class MaterialProcessUpdatePatch
{
    private static void Prefix(MaterialUpdateManager __instance) => MaterialRefresh.EnterProcessUpdate(__instance.World);

    private static Exception? Finalizer(Exception? __exception)
    {
        MaterialRefresh.ExitProcessUpdate();
        return __exception;
    }
}

[HarmonyPatch(typeof(RenderSystem), nameof(RenderSystem.SubmitFrame))]
internal static class SubmitFramePatch
{
    private static bool Prefix(RenderSystem __instance)
    {
        RecoveryCoordinator.Observe(__instance);
        return !RecoveryCoordinator.RenderingSuspended;
    }
}

[HarmonyPatch(typeof(RenderManager), "ComputeRenderUpdate")]
internal static class ComputeRenderUpdatePatch
{
    private static bool Prefix(RenderManager __instance)
    {
        if (RecoveryCoordinator.RenderingSuspended)
            return false;

        using var measure = ResourceMetrics.Measure(ResourceMetrics.Area.EngineHooks);
        RenderStateResync.EnsureCurrent(__instance);
        return true;
    }
}

[HarmonyPatch(typeof(RenderSystem), "HandleFailure")]
internal static class HandleFailurePatch
{
    private static bool Prefix(RenderSystem __instance, Exception exception) =>
        !RecoveryCoordinator.Request(__instance, exception.Message);
}

[HarmonyPatch(typeof(RenderSystem), "HandleCommand")]
internal static class ShutdownRequestPatch
{
    private static bool Prefix(RenderSystem __instance, RendererCommand command) =>
        command is not RendererShutdownRequest || WindowCloseRequests.Handle(__instance);
}

[HarmonyPatch(typeof(RenderSystem), "HandleCommand")]
internal static class PropertyIdPatch
{
    private static void Prefix(RendererCommand command)
    {
        using var measure = ResourceMetrics.Measure(ResourceMetrics.Area.EngineHooks);
        if (command is MaterialPropertyIdResult result)
            PropertyIdMap.EngineReceived(result);

        InFlightRequests.Answered(command);
    }
}

[HarmonyPatch(typeof(Engine), nameof(Engine.ForceCrash))]
internal static class ForceCrashPatch
{
    private static bool Prefix(Engine __instance)
    {
        RenderSystem renderSystem = __instance.RenderSystem;
        if (ForceExit.Requested || __instance.ShutdownRequested)
            return true;

        try
        {
            if (RecoveryCoordinator.ShouldSuppressForceCrash)
            {
                RenderiteRecoveryMod.Msg($"Suppressed the engine's renderer watchdog shutdown (rendering suspended: {RecoveryCoordinator.RenderingSuspended}).");
                RecoveryCoordinator.ReleaseFrameWait(renderSystem);
                return false;
            }
            if (renderSystem.RendererProcess?.HasExited == true
                && RecoveryCoordinator.Request(renderSystem, "renderer watchdog"))
            {
                RecoveryCoordinator.ReleaseFrameWait(renderSystem);
                return false;
            }
        }
        catch (InvalidOperationException) { return false; }
        catch (Exception ex)
        {
            RenderiteRecoveryMod.Error($"Could not decide how to handle the engine's crash request: {ex}");
            return !RecoveryCoordinator.RenderingSuspended;
        }
        return true;
    }
}
