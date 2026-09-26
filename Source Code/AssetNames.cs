using System.Text;
using FrooxEngine;

namespace RenderiteRecovery;

internal static class AssetNames
{
    private const int PathDepth = 4;
    private const int NameLength = 40;

    internal static Dictionary<(string Family, int AssetId), string> Resolve(RenderSystem? system,
        IEnumerable<(string Family, int AssetId)> keys)
    {
        var names = new Dictionary<(string Family, int AssetId), string>();
        if (system is null)
            return names;

        foreach (IGrouping<string, (string Family, int AssetId)> family in keys.Distinct().GroupBy(key => key.Family))
        {
            if (Manager(system, family.Key) is not object manager)
                continue;

            var wanted = family.Select(key => key.AssetId).ToHashSet();
            List<object> assets;
            try { assets = MaterialRefresh.LiveAssets(manager); }
            catch (Exception) { continue; }
            foreach (object item in assets)
            {
                if (item is IRendererAsset renderer && item is Asset asset && wanted.Contains(renderer.AssetId))
                    names[(family.Key, renderer.AssetId)] = Describe(asset);
            }
        }
        return names;
    }

    private static object? Manager(RenderSystem system, string family) => family switch
    {
        "Texture2D" => system.Texture2Ds,
        "Texture3D" => system.Texture3Ds,
        "Cubemap" => system.Cubemaps,
        "Mesh" => system.Meshes,
        "Shader" => system.Shaders,
        "Video" => system.VideoTextures,
        "PointBuffer" => system.PointBuffers,
        "TrailBuffer" => system.TrailBuffers,
        "GaussianSplat" => system.GaussianSplats,
        "DesktopTexture" => system.DesktopTextures,
        "RenderTexture" => system.RenderTextures,
        _ => null
    };

    private static string Describe(Asset asset)
    {
        var parts = new List<string>();
        try
        {
            IWorldElement? owner = asset.Owner as IWorldElement;
            int others = 0;
            if (owner is null)
            {
                List<IWorldElement> requesters = WorldSize.Requesters(asset).OfType<IWorldElement>().ToList();
                owner = requesters.FirstOrDefault();
                others = Math.Max(0, requesters.Count - 1);
            }
            if (owner is not null)
                parts.Add(Where(owner) + (others > 0 ? $" (and {others} more users)" : ""));

            if (asset.AssetURL is Uri url)
                parts.Add(url.ToString());
        }
        catch (Exception ex)
        {
            parts.Add("could not name it: " + ex.Message);
        }
        return parts.Count == 0 ? "no owner (engine-internal)" : string.Join(", ", parts);
    }

    private static string Where(IWorldElement element)
    {
        Slot? slot = element as Slot ?? (element as Component)?.Slot;
        var text = new StringBuilder(element is Slot ? "Slot" : TypeName(element.GetType()));
        if (slot is not null)
            text.Append(" on \"").Append(SlotPath(slot)).Append('"');

        if (slot?.ActiveUser is User user)
            text.Append(", under ").Append(user.UserName ?? "a user");

        if (element.World is World world)
            text.Append(", world ").Append(WorldSize.Plain(world.Name));

        return text.ToString();
    }

    private static string SlotPath(Slot slot)
    {
        var names = new List<string>();
        Slot? current = slot;
        while (current is not null && !current.IsRootSlot && names.Count < PathDepth)
        {
            string name = WorldSize.Plain(current.Name);
            names.Add(name.Length <= NameLength ? name : name[..(NameLength - 1)] + "…");
            current = current.Parent;
        }
        if (current is not null && !current.IsRootSlot)
            names.Add("…");

        names.Reverse();
        return string.Join("/", names);
    }

    private static string TypeName(Type type)
    {
        if (!type.IsGenericType)
            return type.Name;

        string name = type.Name;
        int tick = name.IndexOf('`');
        return (tick > 0 ? name[..tick] : name) + "<" + string.Join(", ", type.GetGenericArguments().Select(TypeName)) + ">";
    }
}
