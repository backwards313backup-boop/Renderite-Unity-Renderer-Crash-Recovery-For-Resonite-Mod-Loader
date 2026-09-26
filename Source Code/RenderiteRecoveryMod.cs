using HarmonyLib;
using ResoniteModLoader;

namespace RenderiteRecovery;

public sealed class RenderiteRecoveryMod : ResoniteMod
{
    internal const string ModVersion = "2.0.0";

    public override string Name => "Renderite Recovery";
    public override string Author => "backwards";
    public override string Version => ModVersion;
    public override string Link => "https://github.com/backwards313backup-boop/";

    internal static readonly Harmony Harmony = new("local.renderite-recovery");

    internal static string Seconds(long value) => value == 1 ? "1 second" : $"{value} seconds";

    internal static void Msg(string message)
    {
        WriteRecoveryLog(message);
        ResoniteMod.Msg(message);
    }

    internal static void Warn(string message)
    {
        WriteRecoveryLog("WARN: " + message);
        ResoniteMod.Warn(message);
    }

    internal static void Error(string message)
    {
        WriteRecoveryLog("ERROR: " + message);
        ResoniteMod.Error(message);
    }

    private static void WriteRecoveryLog(string message) => RecoveryLog.Write(message);

    public override void DefineConfiguration(ModConfigurationDefinitionBuilder builder)
    {
        builder.Version(new Version(1, 0, 0));
        foreach (ModConfigurationKey key in Settings.Keys)
            builder.Key(key);
    }

    public override void OnEngineInit()
    {
        GarbageCollection.Start();
        ThreadCpu.Start();
        Settings.Initialize(GetConfiguration(), this);
        Compatibility.Result compatibility = Compatibility.Check();
        if (!compatibility.CanRun)
        {
            Error("Disabled: this Resonite build is missing engine members the mod depends on. Missing: " + string.Join(", ", compatibility.MissingRequired));
            return;
        }
        Compatibility.LogVersions();

        foreach (Type type in typeof(RenderiteRecoveryMod).Assembly.GetTypes())
        {
            if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0)
                continue;

            if (type == typeof(ExitScreenButton) && !compatibility.ExitButtonAvailable)
            {
                Warn("The Force Exit button is unavailable on this build (missing: " + string.Join(", ", compatibility.MissingOptional) + "). Recovery still works.");
                continue;
            }
            Harmony.CreateClassProcessor(type).Patch();
        }
        RecoveryCoordinator.Start();
        Msg($"Loaded. Renderer recovery journal and process monitor are active. Press {TestCrashHotkey.Description} in the renderer window to test recovery.");
    }
}
