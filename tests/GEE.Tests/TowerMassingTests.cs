using Xunit;

namespace GEE.Tests;

public class TowerMassingTests
{
    private static RawBuilding Tower(double height, long id = 7, double la = 40, double lb = 30)
    {
        var b = new RawBuilding { Id = id, Lat = 40.7, Lon = -74, Height = height, Variant = 1, Style = Buildings.StyleOffice };
        b.Boxes.Add(new Box2(100, 200, 0.4, la, lb));
        return b;
    }

    private static bool Contained(TowerMassing.Tier inner, TowerMassing.Tier outer, double eps = 1e-6) =>
        inner.CA - inner.SA / 2 >= outer.CA - outer.SA / 2 - eps && inner.CA + inner.SA / 2 <= outer.CA + outer.SA / 2 + eps &&
        inner.CB - inner.SB / 2 >= outer.CB - outer.SB / 2 - eps && inner.CB + inner.SB / 2 <= outer.CB + outer.SB / 2 + eps;

    [Theory]
    [InlineData(20, 40, 30)]      // below the threshold
    [InlineData(80, 8, 30)]       // too narrow
    [InlineData(80, 10, 12)]      // too small
    public void Plan_LeavesSmallOrLowBuildingsAlone(double height, double la, double lb)
        => Assert.Empty(TowerMassing.Plan(la, lb, height, 1));

    [Fact]
    public void Plan_IsDeterministic()
        => Assert.Equal(TowerMassing.Plan(40, 30, 120, 99), TowerMassing.Plan(40, 30, 120, 99));

    [Fact]
    public void Plan_ManySeedsAndSizes_StaysInsideAndKeepsTheRoofHeight()
    {
        for (uint seed = 1; seed < 400; seed++)
            foreach (double h in new[] { 36.0, 50, 75, 100, 160, 300 })
                foreach (var (la, lb) in new[] { (12.0, 12.0), (20.0, 15), (30.0, 60), (55.0, 40), (80.0, 80) })
                {
                    var tiers = TowerMassing.Plan(la, lb, h, seed);
                    if (tiers.Count == 0)
                        continue;
                    var footprint = new TowerMassing.Tier(0, 0, 0, la, lb, 0, 0);
                    foreach (var t in tiers)
                    {
                        Assert.True(t.Top > t.Bottom + 0.5, $"{seed} {h} {t}");
                        Assert.True(t.Bottom >= -0.0001 || t.Kind == TowerMassing.Kind.Mast);
                        Assert.True(t.SA > 0.5 && t.SB > 0.5);
                        Assert.True(Contained(t, footprint), $"{seed} {h} {la}x{lb} {t}");
                    }
                    var shafts = tiers.Where(t => t.Kind is TowerMassing.Kind.Shaft or TowerMassing.Kind.Podium).ToList();
                    // Each wall tier sits inside the one below, overlapping it by at most Overlap.
                    for (int i = 1; i < shafts.Count; i++)
                    {
                        var upper = shafts[i];
                        var below = shafts[i - 1];
                        Assert.True(Contained(upper, below), $"{seed} {h} {upper} in {below}");
                        Assert.True(upper.Bottom < below.Top && upper.Bottom >= below.Top - TowerMassing.Overlap - 1e-9);
                        Assert.True(upper.Top >= below.Top);
                    }
                    // The walls end below the OSM roof height; the first crown part ends exactly at it.
                    var crown = tiers.First(t => t.Kind == TowerMassing.Kind.Crown);
                    Assert.Equal(h, crown.Top, 6);
                    Assert.True(shafts.Max(t => t.Top) < h);
                    Assert.True(Contained(crown, shafts.Last()));
                    foreach (var mast in tiers.Where(t => t.Kind == TowerMassing.Kind.Mast))
                        Assert.Contains(tiers.Where(c => c.Kind == TowerMassing.Kind.Crown), c => Contained(mast, c));
                }
    }

    [Fact]
    public void Plan_VariesWithTheSeed_AndTallOnesGetMasts()
    {
        var counts = new HashSet<int>();
        int masts = 0, podiums = 0, single = 0;
        for (uint seed = 1; seed < 200; seed++)
        {
            var t = TowerMassing.Plan(50, 40, 180, seed);
            counts.Add(t.Count);
            masts += t.Count(x => x.Kind == TowerMassing.Kind.Mast);
            podiums += t.Count(x => x.Kind == TowerMassing.Kind.Podium);
            single += t.Count(x => x.Kind == TowerMassing.Kind.Shaft) == 1 ? 1 : 0;
        }
        Assert.True(counts.Count >= 4);
        Assert.Equal(199, masts);              // every 180 m tower has an antenna
        Assert.InRange(podiums, 60, 160);      // about 55 %
        Assert.InRange(single, 20, 160);       // some are single shafts, the rest stepped
        // A 40 m block gets a crown but never a mast.
        var low = TowerMassing.Plan(30, 30, 40, 5);
        Assert.Contains(low, t => t.Kind == TowerMassing.Kind.Crown);
        Assert.DoesNotContain(low, t => t.Kind == TowerMassing.Kind.Mast);
    }

    [Fact]
    public void Eligible_ExcludesLandmarksPitchedRoofsAndFloatingParts()
    {
        Assert.True(TowerMassing.Eligible(Tower(80)));
        Assert.False(TowerMassing.Eligible(Tower(30)));
        Assert.False(TowerMassing.Eligible(Tower(80, id: -3)));
        var launch = Tower(80); launch.LaunchComplex = true;
        Assert.False(TowerMassing.Eligible(launch));
        var floating = Tower(80); floating.MinHeight = 10;
        Assert.False(TowerMassing.Eligible(floating));
        var roofed = Tower(80); roofed.RoofPitch = 0.3;
        Assert.False(TowerMassing.Eligible(roofed));
        var tank = Tower(80); tank.Variant = Buildings.TankVariant;
        Assert.False(TowerMassing.Eligible(tank));
        var road = Tower(80); road.Variant = Buildings.RoadVariant;
        Assert.False(TowerMassing.Eligible(road));
    }

    [Fact]
    public void Parts_BuildOwnBuildingsPerTier_AndHonourTheToggle()
    {
        var b = Tower(150);
        Assert.Null(TowerMassing.Parts(b, b.Boxes, false));
        var parts = TowerMassing.Parts(b, b.Boxes, true)!;
        Assert.NotNull(parts);
        Assert.True(parts.Count >= 3);
        Assert.Equal(1, parts.Count(p => p.IsMain));
        Assert.Equal(150, b.Height);                               // original untouched
        foreach (var p in parts)
        {
            Assert.NotSame(b, p.Building);
            Assert.True(p.Building.Height > p.Building.MinHeight);
            Assert.Equal(0, p.Building.RoofHeight);
            Assert.Equal(b.Lat, p.Building.Lat);
        }
        // Wall tiers keep the facade style; crown and mast are plain.
        Assert.Equal(Buildings.StyleOffice, parts.Single(p => p.IsMain).Building.Style);
        Assert.Equal(Buildings.StyleNone, parts.Last().Building.Style);
        Assert.All(parts, p => Assert.Equal(0.4, p.Box.Angle));
        // Same input, same parts.
        var again = TowerMassing.Parts(b, b.Boxes, true)!;
        Assert.Equal(parts.Select(p => p.Box), again.Select(p => p.Box));
        Assert.Equal(parts.Select(p => p.Building.Height), again.Select(p => p.Building.Height));
    }

    [Fact]
    public void Parts_OffsetsFollowTheBoxRotation_AndSmallSideBoxesStay()
    {
        var b = Tower(120);
        b.Boxes.Add(new Box2(130, 210, 0.4, 12, 10));
        var parts = TowerMassing.Parts(b, b.Boxes, true)!;
        Assert.Equal(b.Boxes[1], parts[0].Box);                    // side box first, untouched
        Assert.Equal(120, parts[0].Building.Height);
        var m = b.Boxes[0];
        foreach (var p in parts.Skip(1))
        {
            double de = p.Box.CenterEast - m.CenterEast, dn = p.Box.CenterNorth - m.CenterNorth;
            double oa = de * Math.Cos(m.Angle) + dn * Math.Sin(m.Angle), ob = -de * Math.Sin(m.Angle) + dn * Math.Cos(m.Angle);
            Assert.True(Math.Abs(oa) + p.Box.LengthA / 2 <= m.LengthA / 2 + 1e-6);
            Assert.True(Math.Abs(ob) + p.Box.LengthB / 2 <= m.LengthB / 2 + 1e-6);
        }
        // Two similar boxes: no massing.
        var wide = Tower(120);
        wide.Boxes.Add(new Box2(0, 0, 0, 40, 30));
        Assert.Null(TowerMassing.Parts(wide, wide.Boxes, true));
    }
}
