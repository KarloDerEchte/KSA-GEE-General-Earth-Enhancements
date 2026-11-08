using Xunit;

namespace GEE.Tests;

public class BuildingTintTests
{
    [Fact]
    public void TagFromSeed_IsInZeroToOneAndStable()
    {
        for (uint s = 0; s < 5000; s++)
        {
            float t = BuildingTint.TagFromSeed(s * 2654435u);
            Assert.True(t > 0f && t < 1f, $"tag {t}");
            Assert.Equal(t, BuildingTint.TagFromSeed(s * 2654435u));
        }
    }

    [Fact]
    public void TagFromSeed_SpreadsOverTheRange()
    {
        var bins = new int[10];
        for (uint s = 1; s <= 20000; s++)
            bins[Math.Min(9, (int)((BuildingTint.TagFromSeed(s) - 0.01f) / 0.98f * 10))]++;
        Assert.All(bins, n => Assert.InRange(n, 1500, 2500));
    }

    [Fact]
    public void Tag_IsZeroWithoutThePatch_AndForNonFacades()
    {
        var wall = new BuildingInstance { Variant = 1, Seed = 42 };
        Assert.Equal(0f, BuildingTint.Tag(in wall));   // RoadWear.Active is false offline
    }

    [Fact]
    public void Functions_BakeTheStrength()
    {
        string f = BuildingTint.Functions(0.5);
        Assert.Contains("0.5", f);
        Assert.DoesNotContain("STRENGTH", f);
    }
}
