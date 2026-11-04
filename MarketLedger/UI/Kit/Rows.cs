using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using static MarketLedger.UI.Theme;

namespace MarketLedger.UI;

/// <summary>One of Settings' tabs: its words and, when it has one, a count after them.</summary>
public readonly record struct TabSpec(string Label, string? Count = null);

/// <summary>Lays things left to right from the cursor, wrapping at the box's edge (a struct: kept in a local, nothing made per frame).</summary>
public struct Wrap
{
    private readonly float gap;
    private readonly Vector2 start;
    private float x;
    private float y;
    private float rowH;

    public Wrap(float gapCss)
    {
        gap = S(gapCss);
        start = Kit.At;
        x = start.X;
        y = start.Y;
    }

    /// <summary>Where the next thing of this size goes.</summary>
    public Vector2 Next(float w, float h)
    {
        if (x > start.X && x + w > Kit.Right + 0.5f)
        {
            x = start.X;
            y += rowH + gap;
            rowH = 0;
        }
        var p = new Vector2(x, y);
        x += w + gap;
        rowH = MathF.Max(rowH, h);
        return p;
    }

    /// <summary>Takes up the rows' room.</summary>
    public void End() => Kit.Reserve(start, Kit.Width, y + rowH - start.Y);
}

/// <summary>
/// The rows the screens share, as the site lays them out: a section's heading with a quiet note on
/// its right ("Waiting runs … 2 from kokos-tools.com"), tabs as a pick-one pill, the round
/// gold icon wells of the boxes, and the row of fact boxes (Steps · Spent · Elapsed).
/// </summary>
public static partial class Kit
{
    /// <summary>A section's heading (Geist 600, 15px) with <paramref name="rest"/> on the right in quiet 12px.</summary>
    public static void Section(string text, string? rest = null)
    {
        var at = At;
        var w = Width;
        var h = Txt.Natural(Face.SansSemibold, 15);
        var restW = rest != null ? Txt.Measure(rest, Face.Sans, 12).X : 0;
        var dl = Dl;
        Txt.Draw(dl, at, Txt.Fit(text, Face.SansSemibold, 15, rest != null ? w - restW - S(12) : w), Face.SansSemibold, 15, Palette.Ink);
        if (rest != null)
            Txt.Draw(dl, new Vector2(at.X + w - restW, at.Y + (h - Txt.Natural(Face.Sans, 12)) / 2), rest, Face.Sans, 12, Palette.Muted);
        Reserve(at, w, h);
    }

    /// <summary>A heading on the left and a link on its right (true when the link is pressed).</summary>
    public static bool SectionLink(string id, string text, string link)
    {
        var at = At;
        var w = Width;
        var h = Txt.Natural(Face.SansSemibold, 15);
        var lw = Txt.Measure(link, Face.Sans, 12).X;
        Txt.Draw(Dl, at, Txt.Fit(text, Face.SansSemibold, 15, w - lw - S(12)), Face.SansSemibold, 15, Palette.Ink);
        var lh = Txt.Natural(Face.Sans, 12);
        var pos = new Vector2(at.X + w - lw, at.Y + (h - lh) / 2);
        var (clicked, hovered, _) = Hit(id, pos - new Vector2(S(4)), new Vector2(lw, lh) + new Vector2(S(8)));
        Txt.Draw(Dl, pos, link, Face.Sans, 12, hovered ? Palette.Ink : Palette.Light);
        Reserve(at, w, h);
        return clicked;
    }

    // ---------------------------------------------------------------------------------------------
    // Tabs.

    public static float TabWidth(TabSpec tab) =>
        S(14) * 2 + Txt.Measure(tab.Label, Face.SansMedium, 13).X + (tab.Count != null ? S(6) + Txt.Measure(tab.Count, Face.Mono, 11).X : 0);

    /// <summary>
    /// Tabs as the site's pick-one pill (36 tall, the strong edge, 3px inside): the picked one a soft
    /// white fill with white words, the others grey. Returns the tab picked (the one given when none was pressed).
    /// </summary>
    public static int Tabs(string id, IReadOnlyList<TabSpec> tabs, int selected)
    {
        var at = At;
        var h = S(36);
        var inner = S(28);
        var total = S(4) * 2 + S(3) * (tabs.Count - 1);
        foreach (var t in tabs) total += TabWidth(t);
        var dl = Dl;
        dl.AddRect(at + new Vector2(0.5f), at + new Vector2(total, h) - new Vector2(0.5f), Palette.U32(Palette.LineStrong), h / 2, ImDrawFlags.None, 1f);
        var result = selected;
        var x = at.X + S(4);
        for (var i = 0; i < tabs.Count; i++)
        {
            var tab = tabs[i];
            var w = TabWidth(tab);
            var p = new Vector2(x, at.Y + S(4));
            var max = p + new Vector2(w, inner);
            var (clicked, hovered, held) = Hit($"{id}{i}", p, new Vector2(w, inner));
            if (clicked) result = i;
            var on = i == selected;
            if (on) dl.AddRectFilled(p, max, Palette.U32(Palette.Picked), inner / 2);
            else if (held || hovered) dl.AddRectFilled(p, max, Palette.U32(held ? Palette.LinePicked : Palette.LineFaint), inner / 2);
            var tx = p.X + S(14);
            tx += Txt.Line(dl, new Vector2(tx, p.Y), tab.Label, Face.SansMedium, 13, on || hovered ? Palette.Ink : Palette.Muted, 28);
            if (tab.Count != null) Txt.Line(dl, new Vector2(tx + S(6), p.Y), tab.Count, Face.Mono, 11, Palette.Faint, 28);
            FocusRing(p, max, inner / 2);
            x += w + S(3);
        }
        Reserve(at, total, h);
        return result;
    }

    // ---------------------------------------------------------------------------------------------
    // Wells and facts.

    /// <summary>
    /// The round icon well of the boxes: its dark gold with the icon in gold (the
    /// favicon's), or grey for something quiet.
    /// </summary>
    public static void IconWell(Vector2 pos, float size, Icon icon, bool quiet = false)
    {
        var dl = Dl;
        var c = pos + new Vector2(size / 2);
        dl.AddCircleFilled(c, size / 2, Palette.U32(quiet ? Palette.Tile : Palette.GoldTile), 40);
        dl.AddCircle(c, size / 2 - 0.5f, Palette.U32(quiet ? Palette.Line : Palette.GoldEdge), 40, 1f);
        var px = MathF.Round(size * 0.45f);
        Icons.Draw(dl, icon, c - new Vector2(px / 2), px, quiet ? Palette.Muted : Palette.Gold);
    }

    /// <summary>One fact in a row of boxes: its label over its value, with a quieter note after the value ("19 to 4:40").</summary>
    public readonly record struct Fact(string Label, string Value, string? Note = null, Vector4? Color = null);

    /// <summary>
    /// The row of fact boxes (Steps · Spent · Elapsed): one rounded box split by
    /// hairlines, each a 12px label over a 15px value. Two to a row when narrow.
    /// </summary>
    public static void Facts(string id, IReadOnlyList<Fact> facts)
    {
        if (facts.Count == 0) return;
        var at = At;
        var w = Width;
        var perRow = w < S(360) ? 2 : facts.Count;
        var rows = (facts.Count + perRow - 1) / perRow;
        var cellW = w / perRow;
        var cellH = S(62);
        var h = cellH * rows;
        var dl = Dl;
        var r = S(Palette.RadiusMd);
        dl.AddRect(at + new Vector2(0.5f), at + new Vector2(w, h) - new Vector2(0.5f), Palette.U32(Palette.Line), r, ImDrawFlags.None, 1f);
        for (var i = 0; i < facts.Count; i++)
        {
            var col = i % perRow;
            var row = i / perRow;
            var p = at + new Vector2(cellW * col, cellH * row);
            if (col > 0) dl.AddLine(new Vector2(p.X + 0.5f, p.Y + 1), new Vector2(p.X + 0.5f, p.Y + cellH - 1), Palette.U32(Palette.Line), 1f);
            if (row > 0 && col == 0) dl.AddLine(new Vector2(at.X + 1, p.Y + 0.5f), new Vector2(at.X + w - 1, p.Y + 0.5f), Palette.U32(Palette.Line), 1f);
            var f = facts[i];
            var inner = cellW - S(28);
            Txt.Draw(dl, p + new Vector2(S(14), S(12)), Txt.Fit(f.Label, Face.Sans, 12, inner), Face.Sans, 12, Palette.Muted);
            var vy = p.Y + S(12) + Txt.Natural(Face.Sans, 12) + S(3);
            var vw = Txt.Draw(dl, new Vector2(p.X + S(14), vy), Txt.Fit(f.Value, Face.SansSemibold, 15, inner), Face.SansSemibold, 15, f.Color ?? Palette.Ink);
            if (f.Note != null && vw + S(6) < inner)
            {
                var ny = vy + Txt.Ascent(Face.SansSemibold, 15) - Txt.Ascent(Face.Sans, 12);
                Txt.Draw(dl, new Vector2(p.X + S(14) + vw + S(6), ny), Txt.Fit(f.Note, Face.Sans, 12, inner - vw - S(6)), Face.Sans, 12, Palette.Muted);
            }
        }
        Reserve(at, w, h);
    }
}
