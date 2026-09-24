using FrooxEngine;
using Renderite.Shared;

namespace RenderiteRecovery;

internal static class RecoveryScreen
{
    internal const string Title = "Renderite Recovery Mode";
    private const int TitleSize = 80;
    private const string TitleColor = "#33FF66";

    internal static RendererInitProgressUpdate Describe(RenderSystem system, int commands)
    {
        int worlds = system.Engine.WorldManager.Worlds.Count(world => world.Render?.IsGeneratingRenderUpdates == true);
        return new RendererInitProgressUpdate
        {
            progress = 1f,
            phase = Title,
            subPhase = $"\n\n          <size={TitleSize}><color={TitleColor}>{Title}</color></size>\nRestoring {commands:N0} assets and rebuilding {worlds} world{(worlds == 1 ? "" : "s")}.\n",
            forceShow = true
        };
    }

    internal sealed class TaskbarProgress
    {
        private const int Steps = 100;
        private readonly RenderiteMessagingHost _host;
        private readonly ulong _total;
        private ulong _lastStep = ulong.MaxValue;

        internal TaskbarProgress(RenderiteMessagingHost host, int total)
        {
            _host = host;
            _total = (ulong)Math.Max(1, total);
            Report(0);
        }

        internal void Report(int completed)
        {
            ulong step = (ulong)Math.Max(0, completed) * Steps / _total;
            if (step == _lastStep)
                return;

            _lastStep = step;
            RecoveryCoordinator.SendRecoveryCommand(_host, new SetTaskbarProgress
            {
                mode = TaskbarProgressBarMode.Normal,
                completed = step,
                total = Steps
            }, true);
        }

        internal void RestoreEngineState(RenderSystem system) =>
            RecoveryCoordinator.SendRecoveryCommand(_host, new SetTaskbarProgress
            {
                mode = system.TaskbarProgressMode,
                completed = system.TaskbarCompleted,
                total = system.TaskbarTotal
            }, true);
    }
}
