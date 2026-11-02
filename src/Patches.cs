using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using HarmonyLib;
using KSA;

namespace GEE;

internal static class Patches
{
    private static bool _renderFailed;

    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            AccessTools.Method(typeof(Celestial), nameof(Celestial.UpdateRenderData), new[] { typeof(IViewport), typeof(int) }),
            postfix: new HarmonyMethod(typeof(Patches), nameof(AfterCelestialRenderData)));

        // Optional: without them there is no night glow.
        try
        {
            harmony.Patch(
                AccessTools.Method(typeof(StaticObjectRenderer), nameof(StaticObjectRenderer.WriteCommandsBlended)),
                postfix: new HarmonyMethod(typeof(Patches), nameof(AfterStaticObjectsBlended)));
            harmony.Patch(
                AccessTools.Method(typeof(Program), nameof(Program.RebuildRenderer)),
                postfix: new HarmonyMethod(typeof(Patches), nameof(AfterRebuildRenderer)));
        }
        catch (Exception e)
        {
            Log.Error($"glow patch failed: {e.Message}");
        }

        // Optional: without it the window is still reachable through settings.json.
        try
        {
            harmony.Patch(
                AccessTools.Method(typeof(Program), "DrawProgramMenusHook"),
                postfix: new HarmonyMethod(typeof(Patches), nameof(AfterProgramMenus)));
        }
        catch (Exception e)
        {
            Log.Error($"menu patch failed: {e.Message}");
        }

        // Optional: without it tree colliders already loaded around vessels stay until their cell reloads.
        try
        {
            harmony.Patch(
                AccessTools.Method(typeof(PhysicsBubble), nameof(PhysicsBubble.PopulatePendingExclusions)),
                postfix: new HarmonyMethod(typeof(Patches), nameof(AfterPendingExclusions)));
        }
        catch (Exception e)
        {
            Log.Error($"clutter exclusion patch failed: {e.Message}");
        }
    }

    private static void AfterPendingExclusions(List<BubbleClutterStatics.ClutterRemoval> destination)
    {
        try
        {
            ClutterClearing.Active?.AddPhysicsExclusions(destination);
        }
        catch (Exception e)
        {
            Log.Error($"clutter exclusion hook failed: {e.Message}");
        }
    }

    private static void AfterCelestialRenderData(Celestial __instance, IViewport viewport)
    {
        if (_renderFailed)
            return;
        try
        {
            using (Perf.Measure("buildings"))
                BuildingRenderer.Render(__instance, viewport);
        }
        catch (Exception e)
        {
            _renderFailed = true;
            Log.Error($"rendering disabled after error: {e}");
        }
    }

    private static bool _glowFailed;

    private static void AfterStaticObjectsBlended(CommandBuffer commandBuffer, IViewport viewport, int frameIndex)
    {
        if (_glowFailed)
            return;
        try
        {
            using (Perf.Measure("windows"))
                FacadeRenderer.Draw(commandBuffer, viewport, frameIndex);
            using (Perf.Measure("satellite"))
                SatelliteImagery.Draw(commandBuffer, viewport, frameIndex);
            using (Perf.Measure("vapour"))
                VaporRenderer.Draw(commandBuffer, viewport, frameIndex);
            using (Perf.Measure("glow"))
                GlowRenderer.Draw(commandBuffer, viewport, frameIndex);
        }
        catch (Exception e)
        {
            _glowFailed = true;
            GlowRenderer.Error = e.Message;
            Log.Error($"night glow disabled after error: {e}");
        }
    }

    private static void AfterRebuildRenderer()
    {
        try
        {
            FacadeRenderer.RebuildFrameResources();
            VaporRenderer.RebuildFrameResources();
            SatelliteImagery.RebuildFrameResources();
            GlowRenderer.RebuildFrameResources();
        }
        catch (Exception e)
        {
            Log.Error($"night glow rebuild failed: {e}");
        }
    }

    private static void AfterProgramMenus()
    {
        GeeMod.Instance?.DrawMenu();
    }
}
