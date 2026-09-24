using Renderite.Shared;

namespace RenderiteRecovery;

internal static class AssetQuarantine
{
    internal readonly record struct Key(string Family, int AssetId)
    {
        public override string ToString() => $"{Family} {AssetId}";
    }

    internal const int BarrierTextureId = 0x7FFF_FF00;

    private const int LiveHistory = 64;
    private const int NewestAlwaysSuspect = 8;
    private const int MaximumNewSuspects = 16;
    private const long LiveWindowMs = 15_000;
    private const int ReportedBlocks = 10;

    private static readonly object Gate = new();
    private static readonly Queue<(Key Key, long Tick)> LiveSends = new();
    private static readonly List<Key> Suspects = new();
    private static readonly Dictionary<Key, string> Quarantined = new();
    private static readonly List<RendererCommand> PendingReplies = new();
    private static int _quarantinedCount;
    private static int _blockedCommands;

    internal sealed record Plan(HashSet<Key> Skipped, List<Key> Probes);

    internal static int BlockedCommands => Volatile.Read(ref _blockedCommands);

    internal static (IReadOnlyList<Key> Quarantined, int Suspects) Snapshot()
    {
        lock (Gate) return (Quarantined.Keys.ToList(), Suspects.Count);
    }

    internal static string? Family(string commandType) => commandType switch
    {
        nameof(SetTexture2DFormat) or nameof(SetTexture2DProperties) or nameof(SetTexture2DData) or nameof(UnloadTexture2D) => "Texture2D",
        nameof(SetTexture3DFormat) or nameof(SetTexture3DProperties) or nameof(SetTexture3DData) or nameof(UnloadTexture3D) => "Texture3D",
        nameof(SetCubemapFormat) or nameof(SetCubemapProperties) or nameof(SetCubemapData) or nameof(UnloadCubemap) => "Cubemap",
        nameof(MeshUploadData) or nameof(MeshUnload) => "Mesh",
        nameof(ShaderUpload) or nameof(ShaderUnload) => "Shader",
        nameof(VideoTextureLoad) or nameof(VideoTextureProperties) or nameof(VideoTextureUpdate)
            or nameof(VideoTextureStartAudioTrack) or nameof(UnloadVideoTexture) => "Video",
        nameof(PointRenderBufferUpload) or nameof(PointRenderBufferUnload) => "PointBuffer",
        nameof(TrailRenderBufferUpload) or nameof(TrailRenderBufferUnload) => "TrailBuffer",
        nameof(GaussianSplatUploadRaw) or nameof(GaussianSplatUploadEncoded) or nameof(UnloadGaussianSplat) => "GaussianSplat",
        _ => null
    };

    private static bool IsUnload(string commandType) => commandType is nameof(UnloadTexture2D) or nameof(UnloadTexture3D)
        or nameof(UnloadCubemap) or nameof(MeshUnload) or nameof(ShaderUnload) or nameof(UnloadVideoTexture)
        or nameof(PointRenderBufferUnload) or nameof(TrailRenderBufferUnload) or nameof(UnloadGaussianSplat);

    private static bool IsFamily(string family) => family is "Texture2D" or "Texture3D" or "Cubemap" or "Mesh"
        or "Shader" or "Video" or "PointBuffer" or "TrailBuffer" or "GaussianSplat";

    internal static Key? KeyOf(CommandJournal.Entry entry) =>
        entry.AssetId is int id && Family(entry.Type) is string family ? new Key(family, id) : null;

    internal static Key? KeyOf(RendererCommand command)
    {
        string type = command.GetType().Name;
        return Family(type) is string family && CommandJournal.AssetId(command) is int id ? new Key(family, id) : null;
    }

    internal static Key? KeyOfRequest(string requestKey)
    {
        string[] parts = requestKey.Split(':');
        return parts.Length >= 2 && IsFamily(parts[0]) && int.TryParse(parts[1], out int id) ? new Key(parts[0], id) : null;
    }

    private static bool IsStreaming(RendererCommand command) =>
        command is PointRenderBufferUpload or TrailRenderBufferUpload or VideoTextureUpdate;

    private static bool IsStreamingFamily(Key key) => key.Family is "PointBuffer" or "TrailBuffer";

    internal static void NoteSent(RendererCommand command)
    {
        if (IsStreaming(command) || KeyOf(command) is not Key key)
            return;

        lock (Gate)
        {
            LiveSends.Enqueue((key, Environment.TickCount64));
            while (LiveSends.Count > LiveHistory)
                LiveSends.Dequeue();
        }
    }

    internal static void RendererDied(IReadOnlyCollection<Key> unanswered, bool deliberate)
    {
        List<Key> recent;
        lock (Gate)
        {
            long now = Environment.TickCount64;
            recent = LiveSends.Where(send => now - send.Tick <= LiveWindowMs).Select(send => send.Key).Reverse().ToList();
            LiveSends.Clear();
        }
        if (deliberate)
        {
            if (Settings.QuarantineAssets)
                RenderiteRecoveryMod.Msg("The renderer was stopped on purpose, so no assets are suspected.");

            return;
        }
        AddSuspects(recent, unanswered.Where(key => !IsStreamingFamily(key)).ToList(), "the renderer was loading them when it failed");
    }

    internal static void ReplayFailed(IReadOnlyList<Key> newestFirst, IReadOnlyCollection<Key> unanswered) =>
        AddSuspects(newestFirst, unanswered, "they were replayed last before the replacement renderer failed");

    private static void AddSuspects(IReadOnlyList<Key> newestFirst, IReadOnlyCollection<Key> unanswered, string why)
    {
        if (!Settings.QuarantineAssets)
            return;

        var chosen = new List<Key>();
        var seen = new HashSet<Key>();
        int rank = 0;
        foreach (Key key in newestFirst)
        {
            if (!seen.Add(key))
                continue;

            if (rank++ < NewestAlwaysSuspect || unanswered.Contains(key))
                chosen.Add(key);
        }
        chosen.AddRange(unanswered.Where(seen.Add));

        var added = new List<Key>();
        lock (Gate)
        {
            foreach (Key key in chosen)
            {
                if (added.Count >= MaximumNewSuspects)
                    break;

                if (Quarantined.ContainsKey(key) || Suspects.Contains(key))
                    continue;

                Suspects.Add(key);
                added.Add(key);
            }
        }
        if (added.Count > 0)
            RenderiteRecoveryMod.Warn($"Suspect assets ({why}): {Describe(added)}. The next attempt replays each of them on its own after everything else, to find the one that crashes the renderer.");
    }

    internal static Plan PlanReplay()
    {
        lock (Gate)
            return new Plan(new HashSet<Key>(Quarantined.Keys), new List<Key>(Suspects));
    }

    internal static void Passed(Key key)
    {
        lock (Gate) Suspects.Remove(key);
    }

    internal static void Confirm(Key key, string failure)
    {
        lock (Gate)
        {
            Suspects.Remove(key);
            Quarantined[key] = failure;
            Volatile.Write(ref _quarantinedCount, Quarantined.Count);
        }
        RenderiteRecoveryMod.Error($"The replacement renderer failed while loading {key} on its own ({failure}). {key} is quarantined for this session: it is skipped in later recoveries, stays blank or invisible, and the engine's updates to it are not sent to the renderer.");
    }

    internal static void RecoverySucceeded(int passed)
    {
        lock (Gate) Suspects.Clear();
        if (passed > 0)
            RenderiteRecoveryMod.Msg($"{passed} suspect assets loaded on their own without a crash, so they are kept.");
    }

    internal static bool Blocks(RendererCommand command)
    {
        if (Volatile.Read(ref _quarantinedCount) == 0 || KeyOf(command) is not Key key)
            return false;

        if (IsUnload(command.GetType().Name))
        {
            bool released;
            lock (Gate)
            {
                released = Quarantined.Remove(key);
                Volatile.Write(ref _quarantinedCount, Quarantined.Count);
            }
            if (released)
                RenderiteRecoveryMod.Msg($"{key} was unloaded by the engine, so it is no longer quarantined.");

            return false;
        }
        lock (Gate)
        {
            if (!Quarantined.ContainsKey(key))
                return false;

            if (InFlightRequests.RequestKey(command) is string request && Reply(request) is RendererCommand reply)
                PendingReplies.Add(reply);
        }
        if (Interlocked.Increment(ref _blockedCommands) <= ReportedBlocks)
            RenderiteRecoveryMod.Msg($"Did not send {command.GetType().Name} for quarantined {key} to the renderer.");

        return true;
    }

    internal static List<RendererCommand> TakeReplies()
    {
        lock (Gate)
        {
            if (PendingReplies.Count == 0)
                return [];

            List<RendererCommand> replies = new(PendingReplies);
            PendingReplies.Clear();
            return replies;
        }
    }

    internal static bool IsQuarantined(string requestKey)
    {
        if (Volatile.Read(ref _quarantinedCount) == 0 || KeyOfRequest(requestKey) is not Key key)
            return false;

        lock (Gate) return Quarantined.ContainsKey(key);
    }

    internal static RendererCommand? Reply(string requestKey)
    {
        string[] parts = requestKey.Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[1], out int id))
            return null;

        TextureUpdateResultType type = default;
        bool texture = parts.Length >= 3 && Enum.TryParse(parts[2], out type);
        return parts[0] switch
        {
            "Mesh" => new MeshUploadResult { assetId = id },
            "Shader" => new ShaderUploadResult { assetId = id },
            "Texture2D" when texture => new SetTexture2DResult { assetId = id, type = type },
            "Texture3D" when texture => new SetTexture3DResult { assetId = id, type = type },
            "Cubemap" when texture => new SetCubemapResult { assetId = id, type = type },
            "PointBuffer" => new PointRenderBufferConsumed { assetId = id },
            "TrailBuffer" => new TrailRenderBufferConsumed { assetId = id },
            "GaussianSplat" => new GaussianSplatResult { assetId = id },
            _ => null
        };
    }

    internal static string Describe(IReadOnlyCollection<Key> keys, int shown = 20) =>
        string.Join(", ", keys.Take(shown)) + (keys.Count > shown ? $", and {keys.Count - shown} more" : "");
}
