using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using static MarketLedger.UI.Theme;

namespace MarketLedger.UI;

/// <summary>
/// Plain text wrapped at word boundaries, its lines kept from frame to frame (by the text, the
/// width, the face and the size), so a paragraph that doesn't change is broken into lines once,
/// not every frame as <see cref="Txt.Flow"/> does. For one colour of words; mixed runs and links
/// stay with <see cref="Txt.Para"/>.
/// </summary>
public static class Lines
{
    private static readonly Dictionary<(string Text, int Width, Face Face, int Px), string[]> Cache = new();

    /// <summary>The lines of <paramref name="text"/> in <paramref name="width"/> screen pixels.</summary>
    public static string[] Of(string text, float width, Face face, int px)
    {
        var key = (text, (int)MathF.Floor(width), face, px);
        if (Cache.TryGetValue(key, out var lines)) return lines;
        // Old widths and texts pile up as the window is resized and the log moves on.
        if (Cache.Count > 600) Cache.Clear();
        return Cache[key] = Break(text, width, face, px);
    }

    private static string[] Break(string text, float width, Face face, int px)
    {
        var result = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var cur = string.Empty;
            foreach (var word in words)
            {
                var candidate = cur.Length == 0 ? word : cur + " " + word;
                if (cur.Length == 0 || Txt.Measure(candidate, face, px).X <= width)
                {
                    cur = candidate;
                    continue;
                }
                result.Add(cur);
                cur = word;
            }
            // A word longer than the line is cut to fit rather than run past the edge.
            result.Add(Txt.Measure(cur, face, px).X > width ? Txt.Fit(cur, face, px, width) : cur);
        }
        return result.ToArray();
    }

    /// <summary>The height of the wrapped text in line boxes <paramref name="lineCss"/> tall.</summary>
    public static float Height(string text, float width, Face face, int px, float lineCss) =>
        Of(text, width, face, px).Length * Txt.LineBox(face, px, lineCss);

    /// <summary>Draws the wrapped text from <paramref name="pos"/>; returns its height.</summary>
    public static float Draw(ImDrawListPtr dl, Vector2 pos, float width, string text, Face face, int px, Vector4 color, float lineCss)
    {
        var lines = Of(text, width, face, px);
        var box = Txt.LineBox(face, px, lineCss);
        for (var i = 0; i < lines.Length; i++)
            Txt.Line(dl, new Vector2(pos.X, pos.Y + box * i), lines[i], face, px, color, lineCss);
        return lines.Length * box;
    }

    /// <summary>A paragraph at the cursor across the box's width (13px, muted unless told); the cursor moves below it.</summary>
    public static void Para(string text, Vector4? color = null, int px = 13, float lineCss = 19.5f, Face face = Face.Sans)
    {
        var at = Kit.At;
        var w = Kit.Width;
        var h = Draw(Kit.Dl, at, w, text, face, px, color ?? Palette.Muted, lineCss);
        Kit.Reserve(at, w, MathF.Max(h, S(1)));
    }
}
