using System.Reflection;
using HarmonyLib;
using KSA;

namespace GEE;

/// <summary>
/// Mixes the trees of the "Low Poly Forest Tree Pack" (99.Miles, CC BY 4.0) into Earth's ground clutter. Trees.xml
/// (written by tools/tree_assets.py) defines them as ordinary clutter objects named GEE_Forest_&lt;Ecotype&gt;_&lt;n&gt;;
/// before the game builds its clutter renderer they are appended to the ecotype of that name. The game then places,
/// fades, shadows and collides them like its own trees: every instance picks one of the ecotype's objects at
/// random (Generate.comp), so the pack's trees take their share of the existing positions. The positions and
/// the mod's tree cover masks do not depend on the object, so nothing else changes.
/// </summary>
internal static class TreePack
{
    private const string Prefix = "GEE_Forest_";
    /// <summary>The game allows 256 objects x LODs per ecotype (prefix sum workgroup), i.e. 51 objects.</summary>
    private const int MaxObjectsPerEcotype = 256 / 5;

    public static Settings? Settings;

    private static readonly MethodInfo? PopulateMaterials =
        AccessTools.Method(typeof(ClutterEcotypeReference), "PopulateMaterialReferences");

    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            AccessTools.Method(typeof(GroundClutterRenderer), nameof(GroundClutterRenderer.PopulatePlanetData)),
            prefix: new HarmonyMethod(typeof(TreePack), nameof(BeforePopulatePlanetData)));
    }

    private static void BeforePopulatePlanetData()
    {
        if (Settings is not { ForestTreePack: true })
            return;
        try
        {
            foreach (Celestial body in Universe.CurrentSystem.All.OfType<Celestial>())
            {
                if (body.Id == "Earth" && body.BodyTemplate.GroundClutterReference is { } clutter)
                    AddTo(clutter);
            }
        }
        catch (Exception e)
        {
            Log.Error($"tree pack: {e}");
        }
    }

    private static void AddTo(GroundClutterReference clutter)
    {
        foreach (ClutterEcotypeReference ecotype in clutter.Ecotypes)
        {
            int added = AppendObjects(ecotype, NamesOf(ecotype));
            if (added > 0)
                Log.Info($"tree pack: {added} trees added to ecotype {ecotype.Name} ({ecotype.ClutterObjects.Count} objects)");
        }
    }

    private static IEnumerable<string> NamesOf(ClutterEcotypeReference ecotype)
    {
        for (int n = 0; ModLibrary.TryGet($"{Prefix}{ecotype.Name}_{n}", out ClutterObjectTemplate? tree) && tree != null; n++)
            yield return $"{Prefix}{ecotype.Name}_{n}";
    }

    /// <summary>
    /// Appends the named clutter objects (from the mod's assets) to an ecotype, up to the game's limit, and refreshes the
    /// ecotype's material list. Returns how many were new. Also used by Scatter.
    /// </summary>
    public static int AppendObjects(ClutterEcotypeReference ecotype, IEnumerable<string> ids)
    {
        int added = 0;
        foreach (string id in ids)
        {
            if (ecotype.ClutterObjects.Count >= MaxObjectsPerEcotype)
                break;
            if (!ModLibrary.TryGet(id, out ClutterObjectTemplate? obj) || obj == null)
                break;
            // PopulatePlanetData runs again when the renderer is rebuilt; the list keeps what was added.
            if (ecotype.ClutterObjects.Contains(obj))
                continue;
            ecotype.ClutterObjects.Add(obj);
            added++;
        }
        if (added > 0)
            PopulateMaterials?.Invoke(ecotype, null);
        return added;
    }
}
