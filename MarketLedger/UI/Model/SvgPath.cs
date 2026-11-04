using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace MarketLedger.UI.Model;

/// <summary>
/// Reads SVG path data ("M12 22a7 7 0 0 0 7-7c0-2-1-3.9-3-5.5…") into polylines, so the window's
/// icons are drawn from the same Lucide paths the mockups use. Supports M L H V C S Q T A Z (and
/// their relative forms); curves and arcs are flattened into short segments. Pure, so it's tested.
/// </summary>
public static class SvgPath
{
    /// <summary>A run of points from one move-to, closed when the path said Z.</summary>
    public sealed record Sub(List<Vector2> Points, bool Closed);

    public static List<Sub> Parse(string d, int curveSegments = 10)
    {
        var subs = new List<Sub>();
        List<Vector2>? pts = null;
        var cur = Vector2.Zero;
        var start = Vector2.Zero;
        var lastCtrl = Vector2.Zero;
        var lastCmd = ' ';
        var i = 0;
        var cmd = ' ';

        void Begin(Vector2 p)
        {
            pts = new List<Vector2> { p };
            subs.Add(new Sub(pts, false));
            start = p;
        }

        void Line(Vector2 p)
        {
            if (pts == null) Begin(cur);
            pts!.Add(p);
        }

        while (true)
        {
            SkipSeparators(d, ref i);
            if (i >= d.Length) break;
            if (char.IsLetter(d[i]) && d[i] is not ('e' or 'E'))
            {
                cmd = d[i++];
            }
            else if (cmd == ' ')
            {
                throw new FormatException($"Path data must start with a command: \"{d}\"");
            }

            var rel = char.IsLower(cmd);
            var at = rel ? cur : Vector2.Zero;
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                {
                    var p = at + Pair(d, ref i);
                    Begin(p);
                    cur = p;
                    cmd = rel ? 'l' : 'L'; // further pairs are line-tos
                    lastCmd = 'M';
                    continue;
                }
                case 'L':
                    cur = at + Pair(d, ref i);
                    Line(cur);
                    break;
                case 'H':
                    cur = new Vector2((rel ? cur.X : 0) + Number(d, ref i), cur.Y);
                    Line(cur);
                    break;
                case 'V':
                    cur = new Vector2(cur.X, (rel ? cur.Y : 0) + Number(d, ref i));
                    Line(cur);
                    break;
                case 'C':
                {
                    var c1 = at + Pair(d, ref i);
                    var c2 = at + Pair(d, ref i);
                    var p = at + Pair(d, ref i);
                    Cubic(cur, c1, c2, p, curveSegments, Line);
                    lastCtrl = c2;
                    cur = p;
                    break;
                }
                case 'S':
                {
                    var c1 = lastCmd is 'C' or 'S' ? 2 * cur - lastCtrl : cur;
                    var c2 = at + Pair(d, ref i);
                    var p = at + Pair(d, ref i);
                    Cubic(cur, c1, c2, p, curveSegments, Line);
                    lastCtrl = c2;
                    cur = p;
                    break;
                }
                case 'Q':
                {
                    var c = at + Pair(d, ref i);
                    var p = at + Pair(d, ref i);
                    Quadratic(cur, c, p, curveSegments, Line);
                    lastCtrl = c;
                    cur = p;
                    break;
                }
                case 'T':
                {
                    var c = lastCmd is 'Q' or 'T' ? 2 * cur - lastCtrl : cur;
                    var p = at + Pair(d, ref i);
                    Quadratic(cur, c, p, curveSegments, Line);
                    lastCtrl = c;
                    cur = p;
                    break;
                }
                case 'A':
                {
                    var rx = Number(d, ref i);
                    var ry = Number(d, ref i);
                    var rotation = Number(d, ref i);
                    var large = Flag(d, ref i);
                    var sweep = Flag(d, ref i);
                    var p = at + Pair(d, ref i);
                    Arc(cur, rx, ry, rotation, large, sweep, p, Line);
                    cur = p;
                    break;
                }
                case 'Z':
                    if (pts != null && subs.Count > 0)
                    {
                        subs[^1] = subs[^1] with { Closed = true };
                        if (pts.Count > 1 && Vector2.Distance(pts[^1], start) < 0.001f) pts.RemoveAt(pts.Count - 1);
                    }
                    cur = start;
                    pts = null;
                    lastCmd = 'Z';
                    cmd = ' ';
                    continue;
                default:
                    throw new FormatException($"Unsupported path command '{cmd}'");
            }
            lastCmd = char.ToUpperInvariant(cmd);
        }
        return subs;
    }

    private static void Cubic(Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p1, int n, Action<Vector2> add)
    {
        for (var s = 1; s <= n; s++)
        {
            var t = s / (float)n;
            var u = 1 - t;
            add(u * u * u * p0 + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * p1);
        }
    }

    private static void Quadratic(Vector2 p0, Vector2 c, Vector2 p1, int n, Action<Vector2> add)
    {
        for (var s = 1; s <= n; s++)
        {
            var t = s / (float)n;
            var u = 1 - t;
            add(u * u * p0 + 2 * u * t * c + t * t * p1);
        }
    }

    /// <summary>An elliptical arc, from the endpoint form to the centre form (SVG 1.1, appendix F.6.5).</summary>
    private static void Arc(Vector2 p0, float rx, float ry, float rotationDeg, bool large, bool sweep, Vector2 p1, Action<Vector2> add)
    {
        if (Vector2.Distance(p0, p1) < 1e-6f) return;
        rx = MathF.Abs(rx);
        ry = MathF.Abs(ry);
        if (rx < 1e-6f || ry < 1e-6f)
        {
            add(p1);
            return;
        }
        var phi = rotationDeg * MathF.PI / 180f;
        var cos = MathF.Cos(phi);
        var sin = MathF.Sin(phi);
        var dx = (p0.X - p1.X) / 2;
        var dy = (p0.Y - p1.Y) / 2;
        var x1 = cos * dx + sin * dy;
        var y1 = -sin * dx + cos * dy;
        var lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
        if (lambda > 1)
        {
            var k = MathF.Sqrt(lambda);
            rx *= k;
            ry *= k;
        }
        var num = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1;
        var den = rx * rx * y1 * y1 + ry * ry * x1 * x1;
        var coef = MathF.Sqrt(MathF.Max(0, num / den)) * (large == sweep ? -1 : 1);
        var cxp = coef * rx * y1 / ry;
        var cyp = coef * -ry * x1 / rx;
        var cx = cos * cxp - sin * cyp + (p0.X + p1.X) / 2;
        var cy = sin * cxp + cos * cyp + (p0.Y + p1.Y) / 2;

        static float Angle(float ux, float uy, float vx, float vy) => MathF.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
        var ux0 = (x1 - cxp) / rx;
        var uy0 = (y1 - cyp) / ry;
        var theta = Angle(1, 0, ux0, uy0);
        var delta = Angle(ux0, uy0, (-x1 - cxp) / rx, (-y1 - cyp) / ry);
        if (!sweep && delta > 0) delta -= 2 * MathF.PI;
        else if (sweep && delta < 0) delta += 2 * MathF.PI;

        var n = Math.Max(2, (int)MathF.Ceiling(MathF.Abs(delta) / (MathF.PI / 16)));
        for (var s = 1; s <= n; s++)
        {
            var t = theta + delta * s / n;
            var ex = rx * MathF.Cos(t);
            var ey = ry * MathF.Sin(t);
            add(s == n ? p1 : new Vector2(cx + cos * ex - sin * ey, cy + sin * ex + cos * ey));
        }
    }

    private static void SkipSeparators(string d, ref int i)
    {
        while (i < d.Length && (char.IsWhiteSpace(d[i]) || d[i] == ',')) i++;
    }

    private static Vector2 Pair(string d, ref int i) => new(Number(d, ref i), Number(d, ref i));

    private static bool Flag(string d, ref int i)
    {
        SkipSeparators(d, ref i);
        if (i >= d.Length || d[i] is not ('0' or '1')) throw new FormatException("Expected an arc flag.");
        return d[i++] == '1';
    }

    private static float Number(string d, ref int i)
    {
        SkipSeparators(d, ref i);
        var from = i;
        if (i < d.Length && d[i] is '-' or '+') i++;
        var dot = false;
        while (i < d.Length && (char.IsDigit(d[i]) || (d[i] == '.' && !dot)))
        {
            if (d[i] == '.') dot = true;
            i++;
        }
        if (i < d.Length && d[i] is 'e' or 'E')
        {
            i++;
            if (i < d.Length && d[i] is '-' or '+') i++;
            while (i < d.Length && char.IsDigit(d[i])) i++;
        }
        if (i == from) throw new FormatException($"Expected a number at {from} in \"{d}\"");
        return float.Parse(d.AsSpan(from, i - from), NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
