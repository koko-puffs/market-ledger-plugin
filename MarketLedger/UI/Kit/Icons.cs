using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using MarketLedger.UI.Model;

namespace MarketLedger.UI;

/// <summary>
/// Lucide's outlined icons, drawn with ImGui's draw lists from their SVG paths
/// (<c>viewBox="0 0 24 24"</c>, <c>stroke-width:1.8</c>, round caps and joins). The stroke is in the
/// icon's 24-unit grid as in SVG, so a 16px icon has the mockup's 1.2px line and a 24px one 1.8px.
///
/// The paths are Lucide's (https://lucide.dev), under the ISC License: copyright (c) for portions of
/// Lucide are held by Cole Bemis 2013-2022 as part of Feather (MIT); all other copyright (c) for
/// Lucide are held by Lucide Contributors 2022. Permission to use, copy, modify, and/or distribute
/// this software for any purpose with or without fee is hereby granted, provided that the above
/// copyright notice and this permission notice appear in all copies. The paths themselves are in
/// <see cref="IconPaths"/> (game-free, so a test reads every one).
/// </summary>
public static class Icons
{
    private static readonly Dictionary<Icon, (List<SvgPath.Sub> Subs, bool Filled)> Parsed = new();

    /// <summary>
    /// The icon's strokes, read once. An icon without paths, or with a path that can't be read, draws
    /// nothing rather than throwing in the middle of a frame (the tests read every path, so it shouldn't).
    /// </summary>
    private static (List<SvgPath.Sub> Subs, bool Filled) Get(Icon icon)
    {
        if (Parsed.TryGetValue(icon, out var p)) return p;
        var subs = new List<SvgPath.Sub>();
        var filled = false;
        if (IconPaths.Source.TryGetValue(icon, out var source))
        {
            filled = source.Filled;
            try
            {
                foreach (var d in source.Paths) subs.AddRange(SvgPath.Parse(d));
            }
            catch (FormatException ex)
            {
                subs.Clear();
                Services.Log.Error(ex, $"Market Ledger: the {icon} icon's path can't be read");
            }
        }
        return Parsed[icon] = (subs, filled);
    }

    /// <summary>Draws an icon with its top-left at <paramref name="pos"/>, <paramref name="size"/> pixels square (already scaled).</summary>
    /// <param name="stroke">The stroke in the icon's 24-unit grid (Lucide's strokeWidth): 1.8, or 1.6 at 18px and up.</param>
    public static void Draw(ImDrawListPtr dl, Icon icon, Vector2 pos, float size, Vector4 color, float stroke = 1.8f)
    {
        var (subs, filled) = Get(icon);
        var k = size / 24f;
        var col = Palette.U32(color);
        var thickness = stroke * k;
        Span<Vector2> buffer = stackalloc Vector2[IconPaths.MaxPoints];
        foreach (var sub in subs)
        {
            var n = Math.Min(sub.Points.Count, buffer.Length);
            if (n == 0) continue;
            for (var i = 0; i < n; i++) buffer[i] = pos + sub.Points[i] * k;
            if (filled)
            {
                dl.AddConvexPolyFilled(ref buffer[0], n, col);
                continue;
            }
            var length = 0f;
            for (var i = 1; i < n; i++) length += Vector2.Distance(buffer[i - 1], buffer[i]);
            if (n == 1 || length < thickness * 0.5f)
            {
                // A dot ("h.01" with a round cap).
                dl.AddCircleFilled(buffer[0], thickness * 0.75f, col, 8);
                continue;
            }
            dl.AddPolyline(ref buffer[0], n, col, sub.Closed ? ImDrawFlags.Closed : ImDrawFlags.None, thickness);
            if (!sub.Closed)
            {
                // Round caps.
                dl.AddCircleFilled(buffer[0], thickness * 0.5f, col, 6);
                dl.AddCircleFilled(buffer[n - 1], thickness * 0.5f, col, 6);
            }
        }
    }
}
