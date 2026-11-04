using System;
using System.Collections.Generic;
using System.Numerics;

namespace MarketLedger.UI.Model;

/// <summary>
/// A dashed edge as the browser draws one: dashes as long as the gaps, walked round a closed outline
/// (corners too), evened out so the pattern closes. Pure, so it's tested.
///
/// Every loop here is bounded by the number of dashes (capped) and the outline's points: 0.3.0 walked
/// the outline by "the distance to the next dash edge", and when float rounding made that distance
/// too small to move, it never ended (and kept adding points), which froze the game on the Start view.
/// </summary>
public static class Dashes
{
    /// <summary>The most dashes ever drawn round one outline.</summary>
    public const int MaxDashes = 4096;

    /// <summary>A rounded rectangle's outline, clockwise from the top-right corner, closed (the last point is the first).</summary>
    public static List<Vector2> RoundedRect(Vector2 min, Vector2 max, float radius, int arcSegments = 10)
    {
        radius = MathF.Max(0, MathF.Min(radius, MathF.Min(max.X - min.X, max.Y - min.Y) / 2));
        arcSegments = Math.Clamp(arcSegments, 1, 64);
        var pts = new List<Vector2>((arcSegments + 1) * 4 + 1);
        void Arc(Vector2 c, float a0, float a1)
        {
            for (var i = 0; i <= arcSegments; i++)
            {
                var a = a0 + (a1 - a0) * i / arcSegments;
                pts.Add(c + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius);
            }
        }
        Arc(new Vector2(max.X - radius, min.Y + radius), -MathF.PI / 2, 0);
        Arc(new Vector2(max.X - radius, max.Y - radius), 0, MathF.PI / 2);
        Arc(new Vector2(min.X + radius, max.Y - radius), MathF.PI / 2, MathF.PI);
        Arc(new Vector2(min.X + radius, min.Y + radius), MathF.PI, MathF.PI * 1.5f);
        pts.Add(pts[0]);
        return pts;
    }

    /// <summary>
    /// The dashes along an outline: about one every <paramref name="period"/> pixels (adjusted so a
    /// whole number fits), each <paramref name="onFraction"/> of it long. Each dash is a polyline.
    /// </summary>
    public static List<Vector2[]> Along(IReadOnlyList<Vector2> outline, float period, float onFraction = 0.5f)
    {
        var dashes = new List<Vector2[]>();
        if (outline.Count < 2 || !(period > 0) || float.IsInfinity(period)) return dashes;

        // How far along the outline each point is.
        var at = new double[outline.Count];
        for (var i = 1; i < outline.Count; i++) at[i] = at[i - 1] + Vector2.Distance(outline[i - 1], outline[i]);
        var total = at[^1];
        if (!(total > 0) || double.IsInfinity(total)) return dashes;

        var count = (int)Math.Clamp(Math.Round(total / period), 1, MaxDashes);
        var step = total / count;
        var dash = step * Math.Clamp(onFraction, 0.05, 1);

        var j = 1; // the outline's segment the current dash starts in; only moves forward
        var points = new List<Vector2>();
        for (var k = 0; k < count; k++)
        {
            var s0 = k * step;
            var s1 = Math.Min(s0 + dash, total);
            while (j < outline.Count - 1 && at[j] < s0) j++;
            points.Clear();
            points.Add(PointAt(outline, at, j, s0));
            var m = j;
            for (; m < outline.Count - 1 && at[m] < s1; m++) points.Add(outline[m]);
            points.Add(PointAt(outline, at, m, s1));
            dashes.Add(points.ToArray());
        }
        return dashes;
    }

    /// <summary>The point at a distance along the outline, within segment <paramref name="j"/> (from point j−1 to j).</summary>
    private static Vector2 PointAt(IReadOnlyList<Vector2> outline, double[] at, int j, double s)
    {
        j = Math.Clamp(j, 1, outline.Count - 1);
        var len = at[j] - at[j - 1];
        var t = len > 0 ? (float)Math.Clamp((s - at[j - 1]) / len, 0, 1) : 0f;
        return Vector2.Lerp(outline[j - 1], outline[j], t);
    }
}
