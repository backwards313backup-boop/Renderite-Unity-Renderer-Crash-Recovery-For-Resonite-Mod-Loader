using System.Diagnostics;
using Elements.Core;
using FrooxEngine;

namespace RenderiteRecovery;

internal static class ForceExit
{
    private static readonly TimeSpan RendererCloseTimeout = TimeSpan.FromSeconds(3);
    private static int _requested;

    internal static bool Requested => Volatile.Read(ref _requested) != 0;

    internal static void Run(Engine engine, string? reason = null)
    {
        if (Interlocked.Exchange(ref _requested, 1) != 0)
            return;

        RenderiteRecoveryMod.Msg(reason ?? "Force exit requested from the exit screen: closing the renderer and terminating without saving.");
        _ = Task.Run(() =>
        {
            try
            {
                RenderSystem renderSystem = engine.RenderSystem;
                Process? renderer = renderSystem.RendererProcess;
                try
                {
                    if (renderer is not null && !renderer.HasExited)
                    {
                        try { renderSystem.ShutdownRenderer(); }
                        catch (Exception ex) { RenderiteRecoveryMod.Warn($"Could not ask the renderer to close: {ex.Message}"); }
                        if (!renderer.WaitForExit(RendererCloseTimeout))
                            renderer.Kill();
                    }
                }
                catch (Exception ex) { RenderiteRecoveryMod.Warn($"Could not stop the renderer: {ex.Message}"); }
                UniLog.Flush();
            }
            finally
            {
                RecoveryLog.Flush(TimeSpan.FromSeconds(2));
                Process.GetCurrentProcess().Kill();
            }
        });
    }
}
