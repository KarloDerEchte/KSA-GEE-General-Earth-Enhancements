using Brutal.Numerics;
using KSA;

namespace GEE;

/// <summary>
/// Which far tiles are worth downloading: those near the camera, and those inside its field of view with a wide margin
/// (so turning the head finds most of them already). Tiles behind the camera are only read when it turns that way; the
/// disk cache then serves them. Used by the far field and the water tiles, which share the same zoom 13 tiles.
/// Main thread only: Begin once per frame before Visible.
/// </summary>
internal static class ViewFilter
{
    /// <summary>Everything this close is always wanted (m).</summary>
    private const double AlwaysMeters = 20000;
    /// <summary>Margin of the field of view, as a share of the tile's distance.</summary>
    private const double MarginShare = 0.50;

    private static Camera? _camera;
    private static double3 _cameraCcf, _rx, _ry, _rz;

    public static void Begin(Camera camera, Celestial body, double3 cameraCcf)
    {
        _camera = camera;
        _cameraCcf = cameraCcf;
        doubleQuat ccf2Cce = body.GetCcf2Cce();
        _rx = Double3Ex.Transform(new double3(1, 0, 0), ccf2Cce);
        _ry = Double3Ex.Transform(new double3(0, 1, 0), ccf2Cce);
        _rz = Double3Ex.Transform(new double3(0, 0, 1), ccf2Cce);
    }

    /// <summary>True if a tile with this centre (CCF, from the planet's centre) and radius should be loaded.</summary>
    public static bool Wanted(double3 centerCcf, double radiusMeters)
    {
        if (_camera is not { } camera)
            return true;
        double3 rel = centerCcf - _cameraCcf;
        double dist = Math.Sqrt(double3.Dot(rel, rel));
        if (dist < AlwaysMeters + radiusMeters)
            return true;
        double3 ego = _rx * rel.X + _ry * rel.Y + _rz * rel.Z;
        return !camera.IsSphereOutsideFov(ego, radiusMeters + dist * MarginShare);
    }
}
