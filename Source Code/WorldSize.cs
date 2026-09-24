using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using Elements.Core;
using FrooxEngine;
using HarmonyLib;
using Renderite.Shared;

namespace RenderiteRecovery;

internal static class WorldSize
{
    internal sealed record Report(string WorldName, Usage Textures, Usage Meshes, Usage Other, bool TexturesOnly)
    {
        internal long TotalBytes => Textures.Bytes + Meshes.Bytes + Other.Bytes;
    }

    internal readonly record struct Usage(long Bytes, int Count);

    private static readonly FieldInfo? VariantLock = AccessTools.Field(typeof(AssetVariantManager), "VariantLock");
    private static readonly ConcurrentDictionary<Type, FieldInfo?> RequestFields = new();
    private static readonly Regex RichTextTag = new("<[^>]*>", RegexOptions.Compiled);

    internal static Report? Measure(RenderSystem system, World world)
    {
        if (world.IsDestroyed)
            return null;

        Dictionary<(string Family, int AssetId), long>? uploads = RecoveryCoordinator.UploadBytesByAsset();

        var textures = new Usage();
        foreach (object item in MaterialRefresh.LiveAssets(system.Texture2Ds))
        {
            if (item is Texture2D texture && UsedBy(texture, world))
                textures = Add(textures, TextureBytes(texture));
        }

        var meshes = new Usage();
        var other = new Usage();
        if (uploads is not null)
        {
            meshes = Sum(system.Meshes, "Mesh", uploads, world, meshes);
            other = Sum(system.Texture3Ds, "Texture3D", uploads, world, other);
            other = Sum(system.Cubemaps, "Cubemap", uploads, world, other);
            other = Sum(system.PointBuffers, "PointBuffer", uploads, world, other);
            other = Sum(system.TrailBuffers, "TrailBuffer", uploads, world, other);
            other = Sum(system.GaussianSplats, "GaussianSplat", uploads, world, other);
        }
        return new Report(PlainName(world), textures, meshes, other, uploads is null);
    }

    private static Usage Sum(object manager, string family, Dictionary<(string Family, int AssetId), long> uploads,
        World world, Usage usage)
    {
        foreach (object item in MaterialRefresh.LiveAssets(manager))
        {
            if (item is not Asset asset || item is not IRendererAsset renderer)
                continue;

            if (!uploads.TryGetValue((family, renderer.AssetId), out long bytes) || !UsedBy(asset, world))
                continue;

            usage = Add(usage, bytes);
        }
        return usage;
    }

    private static Usage Add(Usage usage, long bytes) => bytes > 0 ? new Usage(usage.Bytes + bytes, usage.Count + 1) : usage;

    private static bool UsedBy(Asset asset, World world)
    {
        if (asset.Owner is IWorldElement owner)
            return owner.World == world;

        if (asset.Owner is not null || asset.AssetURL is null)
            return false;

        FieldInfo? field = RequestFields.GetOrAdd(asset.GetType(), static type => AccessTools.Field(type, "requests"));
        if (field?.GetValue(asset) is not IDictionary requests)
            return false;

        object? gate = asset.VariantManager is AssetVariantManager manager ? VariantLock?.GetValue(manager) : null;
        List<object> requesters;
        try
        {
            if (gate is null)
                requesters = requests.Keys.Cast<object>().ToList();
            else
                lock (gate) requesters = requests.Keys.Cast<object>().ToList();
        }
        catch (InvalidOperationException) { return false; }
        return requesters.Any(requester => requester is IWorldElement element && element.World == world);
    }

    private static long TextureBytes(Texture2D texture)
    {
        int2 size = texture.Size;
        if (size.x <= 0 || size.y <= 0)
            return 0;

        TextureFormat format = texture.Format;
        RenderVector2i block = format.BlockSize();
        double bitsPerPixel = format.GetBitsPerPixel();
        int width = size.x, height = size.y;
        long bytes = 0;
        for (int mip = 0; mip < Math.Max(1, texture.MipMapCount); mip++)
        {
            long paddedWidth = (width + block.x - 1) / block.x * (long)block.x;
            long paddedHeight = (height + block.y - 1) / block.y * (long)block.y;
            bytes += (long)Math.Ceiling(paddedWidth * paddedHeight * bitsPerPixel / 8);
            if (width == 1 && height == 1)
                break;

            width = Math.Max(1, width >> 1);
            height = Math.Max(1, height >> 1);
        }
        return bytes;
    }

    private static string PlainName(World world)
    {
        string name = RichTextTag.Replace(world.Name ?? "", "").Trim();
        return name.Length > 0 ? name : "Unnamed world";
    }
}
