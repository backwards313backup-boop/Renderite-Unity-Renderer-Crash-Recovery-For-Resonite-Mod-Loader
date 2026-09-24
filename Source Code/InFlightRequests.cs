using Renderite.Shared;

namespace RenderiteRecovery;

internal static class InFlightRequests
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, int> Outstanding = new();
    private static readonly Dictionary<string, int> Orphaned = new();
    private static readonly List<RendererCommand> Adopted = new();

    internal static void Sent(RendererCommand request)
    {
        if (RequestKey(request) is not string key)
            return;

        lock (Gate) Outstanding[key] = Outstanding.GetValueOrDefault(key) + 1;
    }

    internal static void Answered(RendererCommand reply)
    {
        if (ReplyKey(reply) is not string key)
            return;

        lock (Gate)
        {
            if (!Outstanding.TryGetValue(key, out int count))
                return;

            if (count <= 1)
                Outstanding.Remove(key);
            else
                Outstanding[key] = count - 1;
        }
    }

    internal static HashSet<AssetQuarantine.Key> OutstandingAssets()
    {
        var assets = new HashSet<AssetQuarantine.Key>();
        lock (Gate)
        {
            foreach (string key in Outstanding.Keys)
            {
                if (AssetQuarantine.KeyOfRequest(key) is AssetQuarantine.Key asset)
                    assets.Add(asset);
            }
        }
        return assets;
    }

    internal static void Orphan()
    {
        lock (Gate)
        {
            foreach ((string key, int count) in Outstanding)
                Orphaned[key] = Orphaned.GetValueOrDefault(key) + count;

            Outstanding.Clear();
            if (Orphaned.Count > 0)
                RenderiteRecoveryMod.Msg($"{Orphaned.Values.Sum()} renderer requests were in flight at the failure. Their replies will be passed to the engine after replay.");
        }
    }

    internal static bool Adopt(RendererCommand reply)
    {
        if (ReplyKey(reply) is not string key)
            return false;

        lock (Gate)
        {
            if (!Orphaned.TryGetValue(key, out int count))
                return false;

            if (count <= 1)
                Orphaned.Remove(key);
            else
                Orphaned[key] = count - 1;

            Adopted.Add(reply);
            return true;
        }
    }

    internal static bool Claim(RendererCommand reply)
    {
        if (ReplyKey(reply) is not string key)
            return false;

        lock (Gate)
        {
            if (!Orphaned.TryGetValue(key, out int count))
                return false;

            if (count <= 1)
                Orphaned.Remove(key);
            else
                Orphaned[key] = count - 1;

            return true;
        }
    }

    internal static void Deliver(Action<RendererCommand> forward)
    {
        var replies = new List<RendererCommand>();
        List<string> unanswered;
        lock (Gate)
        {
            replies.AddRange(Adopted);
            Adopted.Clear();
            foreach (string key in Orphaned.Keys.ToList())
            {
                Func<RendererCommand?>? synthesize = key.StartsWith(LightsPrefix, StringComparison.Ordinal)
                    ? () => new LightsBufferRendererConsumed { globalUniqueId = int.Parse(key.AsSpan(LightsPrefix.Length)) }
                    : key.StartsWith(MaterialBatchPrefix, StringComparison.Ordinal)
                        ? () => new MaterialsUpdateBatchResult { updateBatchId = int.Parse(key.AsSpan(MaterialBatchPrefix.Length)) }
                        : AssetQuarantine.IsQuarantined(key)
                            ? () => AssetQuarantine.Reply(key)
                            : null;
                if (synthesize is null || synthesize() is null)
                    continue;

                for (int i = 0; i < Orphaned[key]; i++)
                    replies.Add(synthesize()!);

                Orphaned.Remove(key);
            }
            unanswered = Orphaned.SelectMany(pair => Enumerable.Repeat(pair.Key, pair.Value)).ToList();
        }

        int delivered = 0;
        foreach (RendererCommand reply in replies)
        {
            try
            {
                if (reply is MaterialPropertyIdResult propertyIds)
                    PropertyIdMap.TranslateToEngine(propertyIds);

                forward(reply);
                delivered++;
            }
            catch (Exception ex)
            {
                RenderiteRecoveryMod.Warn($"Could not deliver {reply.GetType().Name} to the engine: {ex.Message}");
            }
        }
        if (delivered > 0)
            RenderiteRecoveryMod.Msg($"Delivered {delivered} replies to requests that were in flight at the failure.");

        if (unanswered.Count > 0)
            RenderiteRecoveryMod.Msg($"{unanswered.Count} requests in flight at the failure are still waiting for the replacement renderer: " + string.Join(", ", unanswered.Take(10)) + (unanswered.Count > 10 ? ", ..." : "") + ".");
    }

    private const string LightsPrefix = "Lights:";
    private const string MaterialBatchPrefix = "MaterialBatch:";

    internal static string? RequestKey(RendererCommand request) => request switch
    {
        MeshUploadData value => $"Mesh:{value.assetId}",
        ShaderUpload value => $"Shader:{value.assetId}",
        SetTexture2DFormat value => $"Texture2D:{value.assetId}:{TextureUpdateResultType.FormatSet}",
        SetTexture2DProperties { applyImmediatelly: true } value => $"Texture2D:{value.assetId}:{TextureUpdateResultType.PropertiesSet}",
        SetTexture2DData value => $"Texture2D:{value.assetId}:{TextureUpdateResultType.DataUpload}",
        SetTexture3DFormat value => $"Texture3D:{value.assetId}:{TextureUpdateResultType.FormatSet}",
        SetTexture3DProperties { applyImmediatelly: true } value => $"Texture3D:{value.assetId}:{TextureUpdateResultType.PropertiesSet}",
        SetTexture3DData value => $"Texture3D:{value.assetId}:{TextureUpdateResultType.DataUpload}",
        SetCubemapFormat value => $"Cubemap:{value.assetId}:{TextureUpdateResultType.FormatSet}",
        SetCubemapProperties { applyImmediatelly: true } value => $"Cubemap:{value.assetId}:{TextureUpdateResultType.PropertiesSet}",
        SetCubemapData value => $"Cubemap:{value.assetId}:{TextureUpdateResultType.DataUpload}",
        SetRenderTextureFormat value => $"RenderTexture:{value.assetId}",
        MaterialsUpdateBatch value => $"MaterialBatch:{value.updateBatchId}",
        MaterialPropertyIdRequest value => $"PropertyIds:{value.requestId}",
        VideoTextureLoad value => $"Video:{value.assetId}",
        PointRenderBufferUpload value => $"PointBuffer:{value.assetId}",
        TrailRenderBufferUpload value => $"TrailBuffer:{value.assetId}",
        GaussianSplatUpload value => $"GaussianSplat:{value.assetId}",
        LightsBufferRendererSubmission value => $"{LightsPrefix}{value.lightsBufferUniqueId}",
        _ => null
    };

    private static string? ReplyKey(RendererCommand reply) => reply switch
    {
        MeshUploadResult value => $"Mesh:{value.assetId}",
        ShaderUploadResult value => $"Shader:{value.assetId}",
        SetTexture2DResult value => $"Texture2D:{value.assetId}:{value.type}",
        SetTexture3DResult value => $"Texture3D:{value.assetId}:{value.type}",
        SetCubemapResult value => $"Cubemap:{value.assetId}:{value.type}",
        RenderTextureResult value => $"RenderTexture:{value.assetId}",
        MaterialsUpdateBatchResult value => $"MaterialBatch:{value.updateBatchId}",
        MaterialPropertyIdResult value => $"PropertyIds:{value.requestId}",
        VideoTextureReady value => $"Video:{value.assetId}",
        PointRenderBufferConsumed value => $"PointBuffer:{value.assetId}",
        TrailRenderBufferConsumed value => $"TrailBuffer:{value.assetId}",
        GaussianSplatResult value => $"GaussianSplat:{value.assetId}",
        LightsBufferRendererConsumed value => $"{LightsPrefix}{value.globalUniqueId}",
        _ => null
    };
}
