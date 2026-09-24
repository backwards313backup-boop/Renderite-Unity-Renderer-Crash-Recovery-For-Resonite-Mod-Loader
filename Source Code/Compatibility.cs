using System.Reflection;
using FrooxEngine;
using HarmonyLib;
using Renderite.Shared;

namespace RenderiteRecovery;

internal static class Compatibility
{
    internal const string TestedEngineVersion = "2026.9.18.82";
    internal const string TestedSharedVersion = "1.3.2.0";

    internal sealed record Result(IReadOnlyList<string> MissingRequired, IReadOnlyList<string> MissingOptional)
    {
        internal bool CanRun => MissingRequired.Count == 0;
        internal bool ExitButtonAvailable => MissingOptional.Count == 0;
    }

    internal static Result Check()
    {
        var required = new List<string>();
        var optional = new List<string>();

        Method(required, typeof(RenderiteMessagingHost), "SendCommand");
        Method(required, typeof(RenderSystem), "WaitForFrameBegin");
        Method(required, typeof(RenderSystem), "SubmitFrame");
        Method(required, typeof(RenderSystem), "HandleFailure");
        Method(required, typeof(RenderSystem), "HandleCommand");
        Method(required, typeof(RenderManager), "ComputeRenderUpdate");
        Method(required, typeof(Engine), "ForceCrash");

        Field(required, typeof(RenderSystem), "_messagingHost");
        Field(required, typeof(RenderSystem), "_sharedMemory");
        Field(required, typeof(RenderSystem), "_actualHeadOutputDevice");
        Field(required, typeof(RenderSystem), "_frameStartEvent");
        Method(required, typeof(RenderSystem), "RendererWatchdog");
        Method(required, typeof(RenderSystem), "AllocateBlock");
        Setter(required, typeof(RenderSystem), nameof(RenderSystem.RendererProcess));
        Setter(required, typeof(RenderSystem), nameof(RenderSystem.State));
        Field(required, typeof(RenderiteMessagingHost), "_primary");
        Field(required, typeof(RenderiteMessagingHost), "_background");
        Field(required, typeof(SharedMemoryManager), "_managers");

        Field(required, typeof(RenderManager), "_stagedRenderSpaceUpdate");
        Field(required, typeof(RenderTransformManager), "_renderableSlots");
        Field(required, typeof(RenderTransformManager), "_renderableSlotsToAddOrRemove");
        Setter(required, typeof(RenderTransformManager), "EstimatedAdditionCount");
        Setter(required, typeof(Slot), "RenderTransformIndex");
        Setter(required, typeof(RenderableComponent), "RenderableIndex");
        Field(required, typeof(RenderableComponentManager<,>), "renderables");
        Method(required, typeof(RenderableComponentManager<,>), "RenderableCleared");
        Method(required, typeof(RenderableComponentManager<,>), "RenderableAddedOrRemoved");
        Method(required, typeof(ReflectionProbe), "MarkChangesRender");
        Field(required, typeof(ReflectionProbeSH2Manager), "_scheduledTasks");
        Method(required, typeof(ReflectionProbeSH2Manager), "HandleResults");

        Method(required, typeof(MaterialUpdateManager), "ProcessUpdate");
        Field(required, typeof(RenderMaterialManager), "Materials");
        Field(required, typeof(RenderMaterialManager), "PropertyBlocks");
        Field(required, typeof(RenderAssetManager<>), "_assetsById");
        Field(required, typeof(RenderAssetManager<>), "_lock");
        Method(required, typeof(MaterialProviderBase<>), "AssetCreated");
        Method(required, typeof(MaterialProviderBase<>), "UpdateAsset");
        Property(required, typeof(MaterialProviderBase<>), "Asset");
        Property(required, typeof(Asset), "Owner");

        Method(optional, typeof(ExitScreen), "OnStart");
        Property(optional, typeof(RadiantDashScreen), "ScreenCanvas");

        return new Result(required, optional);
    }

    internal static void LogVersions()
    {
        string engine = typeof(Engine).Assembly.GetName().Version?.ToString() ?? "unknown";
        string shared = typeof(RendererCommand).Assembly.GetName().Version?.ToString() ?? "unknown";
        if (engine != TestedEngineVersion || shared != TestedSharedVersion)
            RenderiteRecoveryMod.Warn($"Running on FrooxEngine {engine} / Renderite.Shared {shared}, but this version was tested with {TestedEngineVersion} / {TestedSharedVersion}. The required members are present, but renderer behavior may have changed.");
    }

    private static void Method(List<string> missing, Type type, string name)
    {
        if (AccessTools.Method(type, name) is null)
            missing.Add($"{type.Name}.{name}()");
    }

    private static void Field(List<string> missing, Type type, string name)
    {
        if (AccessTools.Field(type, name) is null)
            missing.Add($"{type.Name}.{name}");
    }

    private static void Property(List<string> missing, Type type, string name)
    {
        if (AccessTools.Property(type, name) is null)
            missing.Add($"{type.Name}.{name}");
    }

    private static void Setter(List<string> missing, Type type, string name)
    {
        if (AccessTools.Property(type, name)?.GetSetMethod(true) is null)
            missing.Add($"{type.Name}.{name} (setter)");
    }
}
