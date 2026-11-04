using System.Numerics;
using MarketLedger.UI;
using MarketLedger.UI.Model;
using Xunit;

namespace MarketLedger.Tests;

/// <summary>
/// The window's game-free parts: every icon's path is read here, because a path the reader can't take
/// would throw the first time its icon is drawn (in the middle of a frame), and the dashed edge.
/// </summary>
public class IconPathTests
{
    [Fact]
    public void EveryIconHasPaths()
    {
        foreach (var icon in Enum.GetValues<Icon>())
        {
            Assert.True(IconPaths.Source.TryGetValue(icon, out var source), $"{icon} has no paths");
            Assert.True(source.Paths.Length > 0, $"{icon} has no paths");
        }
    }

    [Fact]
    public void EveryIconPathReads()
    {
        foreach (var (icon, source) in IconPaths.Source)
            foreach (var d in source.Paths)
            {
                List<SvgPath.Sub> subs;
                try
                {
                    subs = SvgPath.Parse(d);
                }
                catch (FormatException ex)
                {
                    throw new FormatException($"{icon}: {ex.Message}");
                }
                Assert.True(subs.Count > 0, $"{icon}: \"{d}\" draws nothing");
                foreach (var sub in subs)
                {
                    // The icon is drawn from a buffer of this many points: more would be cut off.
                    Assert.True(sub.Points.Count is > 0 and <= IconPaths.MaxPoints, $"{icon}: a stroke of {sub.Points.Count} points");
                    foreach (var p in sub.Points)
                        Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y) && p.X is >= -1 and <= 25 && p.Y is >= -1 and <= 25,
                            $"{icon}: the point {p} is outside the 24-unit grid");
                }
            }
    }

    [Fact]
    public void ACircleIsOneClosedRound()
    {
        var subs = SvgPath.Parse(IconPaths.Circle(12, 12, 10));
        var sub = Assert.Single(subs);
        Assert.True(sub.Closed);
        foreach (var p in sub.Points)
            Assert.True(MathF.Abs(Vector2.Distance(p, new Vector2(12, 12)) - 10) < 0.01f, $"{p} isn't on the circle");
    }

    [Fact]
    public void ARoundedRectStaysInItsBox()
    {
        var sub = Assert.Single(SvgPath.Parse(IconPaths.Rect(6, 4, 4, 16, 1)));
        Assert.True(sub.Closed);
        foreach (var p in sub.Points)
            Assert.True(p.X is >= 5.99f and <= 10.01f && p.Y is >= 3.99f and <= 20.01f, $"{p} is outside the rect");
    }

    [Theory]
    [InlineData("")]
    [InlineData("M3 6H3.01")]
    [InlineData("m6 6 12 12")]
    [InlineData("M1e1 2E-1L3,4")]
    [InlineData("M2 12s3-7 10-7 10 7 10 7-3 7-10 7-10-7-10-7Z")]
    [InlineData("M0 0Q5 5 10 0T20 0")]
    public void GoodPathsRead(string d) => SvgPath.Parse(d);

    [Theory]
    [InlineData("3 4")]
    [InlineData("M")]
    [InlineData("M1")]
    [InlineData("M1 2X3 4")]
    [InlineData("M1 2A1 1 0 2 1 3 4")]
    [InlineData("M1 2L-")]
    [InlineData("M0 0Z 3 4")]
    public void BadPathsSaySo(string d) => Assert.Throws<FormatException>(() => SvgPath.Parse(d));

    [Fact]
    public void LinesAfterAMoveAreLineTos()
    {
        var sub = Assert.Single(SvgPath.Parse("M7 4 20 12 7 20 7 4Z"));
        Assert.True(sub.Closed);
        // The last point, back at the start, is folded into the close.
        Assert.Equal(3, sub.Points.Count);
    }
}

public class DashTests
{
    [Fact]
    public void ARoundedRectIsClosed()
    {
        var outline = Dashes.RoundedRect(Vector2.Zero, new Vector2(100, 40), 12);
        Assert.Equal(outline[0], outline[^1]);
    }

    [Fact]
    public void DashesGoRoundAndStop()
    {
        var outline = Dashes.RoundedRect(Vector2.Zero, new Vector2(100, 40), 12);
        var dashes = Dashes.Along(outline, 6);
        Assert.True(dashes.Count is > 10 and <= Dashes.MaxDashes);
        foreach (var dash in dashes) Assert.True(dash.Length >= 2);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void NoDashesForABadPeriod(float period) =>
        Assert.Empty(Dashes.Along(Dashes.RoundedRect(Vector2.Zero, new Vector2(10, 10), 2), period));

    [Fact]
    public void ATinyPeriodIsCapped()
    {
        var dashes = Dashes.Along(Dashes.RoundedRect(Vector2.Zero, new Vector2(10000, 10000), 2), 0.001f);
        Assert.True(dashes.Count <= Dashes.MaxDashes);
    }
}
