using System.Diagnostics;
using FrooxEngine;
using Renderite.Shared;

namespace RenderiteRecovery;

internal static class TestCrashHotkey
{
    internal const string Description = "Ctrl + Shift + Alt + R";
    private static bool _wasHeld;

    internal static void Poll(RenderSystem renderSystem)
    {
        InputInterface? input = renderSystem.Engine?.InputInterface;
        if (input is null)
            return;

        bool held = input.GetKey(Key.Control) && input.GetKey(Key.Shift) && input.GetKey(Key.Alt) && input.GetKey(Key.R);
        bool pressed = held && !_wasHeld;
        _wasHeld = held;
        if (!pressed || RecoveryCoordinator.RenderingSuspended || ForceExit.Requested || renderSystem.Engine!.ShutdownRequested)
            return;

        Process? renderer = renderSystem.RendererProcess;
        try
        {
            if (renderer is null || renderer.HasExited)
                return;

            RenderiteRecoveryMod.Warn($"{Description} pressed: terminating the renderer (PID {renderer.Id}) to test recovery.");
            RecoveryCoordinator.NoteDeliberateKill(renderer.Id);
            renderer.Kill();
        }
        catch (Exception ex)
        {
            RenderiteRecoveryMod.Warn($"Could not terminate the renderer for the recovery test: {ex.Message}");
        }
    }
}
