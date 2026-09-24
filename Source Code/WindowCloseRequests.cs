using System.Diagnostics;
using FrooxEngine;

namespace RenderiteRecovery;

internal static class WindowCloseRequests
{
    private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(3);
    private static int _pending;

    internal static bool Handle(RenderSystem renderSystem)
    {
        Engine engine = renderSystem.Engine;
        if (engine.ShutdownRequested || ForceExit.Requested)
            return true;

        if (Userspace.IsExitingApp)
        {
            RenderiteRecoveryMod.Msg("Renderer window closed while Resonite is already exiting, quitting now.");
            return true;
        }
        if (Interlocked.Exchange(ref _pending, 1) != 0)
            return false;

        Process? renderer = renderSystem.RendererProcess;
        RenderiteRecoveryMod.Msg($"Renderer window close requested. Waiting {GracePeriod.TotalSeconds:F0} seconds to tell a window close from a forced termination.");
        _ = Task.Run(async () =>
        {
            try
            {
                if (renderer is not null && await HasExitedWithin(renderer, GracePeriod).ConfigureAwait(false))
                {
                    RenderiteRecoveryMod.Msg("The renderer was terminated after its close request (for example Task Manager's End task), so it is treated as a crash.");
                    return;
                }
                if (engine.ShutdownRequested || ForceExit.Requested)
                    return;

                RenderiteRecoveryMod.Msg("Renderer window was closed, Resonite is exiting normally (not treated as a crash).");
                engine.RequestShutdown();
            }
            catch (Exception ex)
            {
                RenderiteRecoveryMod.Error($"Could not process the renderer window close: {ex}");
            }
            finally
            {
                Volatile.Write(ref _pending, 0);
            }
        });
        return false;
    }

    private static async Task<bool> HasExitedWithin(Process process, TimeSpan timeout)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException) { return false; }
        catch (InvalidOperationException) { return true; }
    }
}
