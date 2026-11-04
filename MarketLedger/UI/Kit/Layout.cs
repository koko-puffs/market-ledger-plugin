using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using static MarketLedger.UI.Theme;

namespace MarketLedger.UI;

/// <summary>A chip's colour: the outline, green (done, found), gold (live, yours), or red (failed, missing).</summary>
public enum ChipKind
{
    Plain,
    Ok,
    Gold,
    Bad,
    Quiet,
}

/// <summary>
/// The wider layout's parts, from Glamour Studio's plugin kit: panes (a region that scrolls on its
/// own), cards drawn at a rectangle, two columns side by side, chips in their colours and sizes,
/// small mono labels, links and dots. Columns here are begun and ended rather than given lambdas,
/// so a frame lays them without making closures.
/// </summary>
public static partial class Kit
{
    // ---------------------------------------------------------------------------------------------
    // Panes.

    internal static readonly Stack<(float PadBottom, Vector2 Min, Vector2 Size)> Panes = new();

    /// <summary>
    /// A pane at <paramref name="min"/>, <paramref name="size"/> big: its own child window (it scrolls
    /// when what's in it is taller, unless <paramref name="scroll"/> is false), with padding in CSS
    /// pixels. Lay rows into it as into the window; end it with <see cref="EndPane"/>.
    /// </summary>
    public static void BeginPane(string id, Vector2 min, Vector2 size, float padX, float padTop, float padBottom, bool scroll = true)
    {
        ImGui.SetCursorScreenPos(min);
        size = Vector2.Max(size, new Vector2(S(20)));
        ImGui.BeginChild(id, size, false, scroll ? ImGuiWindowFlags.None : ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        Frame.Open.Push(false);
        var at = ImGui.GetCursorScreenPos();
        Panes.Push((padBottom, min, size));
        PushBox(at.X + S(padX), ImGui.GetContentRegionAvail().X - S(padX) * 2);
        Reserve(at, 1, S(padTop));
    }

    public static void EndPane()
    {
        var (padBottom, _, _) = Panes.Pop();
        Gap(padBottom);
        PopBox();
        if (Frame.Open.Count > 0) Frame.Open.Pop();
        ImGui.EndChild();
    }

    /// <summary>A card (fill, 1px edge, radius) drawn at a rectangle, behind what's laid in it.</summary>
    public static void Card(Vector2 min, Vector2 max, Vector4? fill = null, Vector4? edge = null, float radius = Palette.RadiusLg)
    {
        var r = S(radius);
        var dl = Dl;
        var f = fill ?? Palette.Card;
        if (f.W > 0) dl.AddRectFilled(min, max, Palette.U32(f), r);
        dl.AddRect(min + new Vector2(0.5f), max - new Vector2(0.5f), Palette.U32(edge ?? Palette.Line), r, ImDrawFlags.None, 1f);
    }

    internal static readonly Stack<(Vector2 At, float Width, float Height)> ScrollCards = new();

    /// <summary>
    /// A card at the cursor, <paramref name="height"/> tall, whose inside scrolls on its own (the log,
    /// a long list). Padding in CSS pixels. End it with <see cref="EndScrollCard"/>.
    /// </summary>
    public static void BeginScrollCard(string id, float height, float pad, Vector4? fill = null, Vector4? edge = null, float radius = Palette.RadiusLg)
    {
        var at = At;
        var w = Width;
        Card(at, at + new Vector2(w, height), fill, edge, radius);
        ScrollCards.Push((at, w, height));
        BeginPane(id, at + new Vector2(1), new Vector2(w - 2, height - 2), pad, pad, pad);
    }

    public static void EndScrollCard()
    {
        EndPane();
        var (at, w, h) = ScrollCards.Pop();
        Reserve(at, w, h);
    }

    // ---------------------------------------------------------------------------------------------
    // Two columns.

    private static readonly Stack<(Vector2 At, float Width, float LeftWidth, float Gap, float LeftBottom)> ColumnSets = new();

    /// <summary>
    /// Two columns side by side from the cursor: the right one <paramref name="rightWidth"/> wide
    /// (screen pixels), the left one the rest, <paramref name="gapCss"/> between. Lay the left one,
    /// call <see cref="NextColumn"/>, lay the right one, then <see cref="EndColumns"/>: the cursor goes
    /// under the taller.
    /// </summary>
    public static void BeginColumns(float rightWidth, float gapCss)
    {
        var at = At;
        var w = Width;
        var gap = S(gapCss);
        var lw = MathF.Max(S(40), w - gap - rightWidth);
        ColumnSets.Push((at, w, lw, gap, at.Y));
        PushBox(at.X, lw);
        ImGui.SetCursorScreenPos(at);
    }

    public static void NextColumn()
    {
        var (at, w, lw, gap, _) = ColumnSets.Pop();
        var bottom = ImGui.GetCursorScreenPos().Y;
        PopBox();
        ColumnSets.Push((at, w, lw, gap, bottom));
        PushBox(at.X + lw + gap, w - lw - gap);
        ImGui.SetCursorScreenPos(new Vector2(at.X + lw + gap, at.Y));
    }

    public static void EndColumns()
    {
        var (at, w, _, _, leftBottom) = ColumnSets.Pop();
        var rightBottom = ImGui.GetCursorScreenPos().Y;
        PopBox();
        Reserve(at, w, MathF.Max(leftBottom, rightBottom) - at.Y);
    }

    // ---------------------------------------------------------------------------------------------
    // Chips in colours and sizes.

    private static (float H, float Pad, int Px) ChipSpec(int size) => size switch
    {
        18 => (18, 6, 11),
        22 => (22, 8, 11),
        _ => (24, 10, 12),
    };

    public static float ChipW(string text, bool tick = false, int size = 24, Face face = Face.Sans)
    {
        var (_, pad, px) = ChipSpec(size);
        return S(pad) * 2 + (tick ? S(12 + 6) : 0) + Txt.Measure(text, face, px).X;
    }

    /// <summary>A chip (24, 22 or 18 tall) in its colour, with a tick before its words if asked. Returns its width.</summary>
    public static float Chip(Vector2 pos, string text, ChipKind kind, bool tick = false, int size = 24, float maxWidth = 0, Face face = Face.Sans)
    {
        var (hc, pad, px) = ChipSpec(size);
        var h = S(hc);
        if (maxWidth > 0 && ChipW(text, tick, size, face) > maxWidth)
            text = Txt.Fit(text, face, px, MathF.Max(S(10), maxWidth - S(pad) * 2 - (tick ? S(18) : 0)));
        var w = ChipW(text, tick, size, face);
        var (fill, edge, ink) = kind switch
        {
            ChipKind.Ok => (Palette.SuccessWash, Palette.SuccessEdge, Palette.Success),
            ChipKind.Gold => (Palette.GoldWash, Palette.GoldEdge, Palette.GoldSoft),
            ChipKind.Bad => (Palette.DangerWash, Palette.DangerEdge, Palette.Danger),
            ChipKind.Quiet => (Palette.Tile, Vector4.Zero, Palette.Soft),
            _ => (Vector4.Zero, Palette.LineStrong, Palette.Soft),
        };
        var dl = Dl;
        var r = kind == ChipKind.Quiet ? S(6) : h / 2;
        if (fill.W > 0) dl.AddRectFilled(pos, pos + new Vector2(w, h), Palette.U32(fill), r);
        if (edge.W > 0) dl.AddRect(pos + new Vector2(0.5f), pos + new Vector2(w, h) - new Vector2(0.5f), Palette.U32(edge), r, ImDrawFlags.None, 1f);
        var x = pos.X + S(pad);
        if (tick)
        {
            Icons.Draw(dl, Icon.Check, new Vector2(x, pos.Y + (h - S(12)) / 2), S(12), ink, 2.2f);
            x += S(12 + 6);
        }
        Txt.Line(dl, new Vector2(x, pos.Y), text, face, px, ink, hc);
        return w;
    }

    // ---------------------------------------------------------------------------------------------
    // Small things.

    /// <summary>
    /// A small label in spaced capitals ("NOW", "WHILE RUNNING"): Geist Mono 11px, .06em, muted (or a
    /// colour), as the site's labels. The text is given in capitals already, so nothing is made per frame.
    /// Returns its width.
    /// </summary>
    public static float Label(Vector2 pos, string caps, Vector4? color = null) => Txt.Spaced(Dl, pos, caps, Face.Mono, 11, color ?? Palette.Muted, 0.06f);

    /// <summary>A label on its own row.</summary>
    public static void LabelRow(string caps, Vector4? color = null)
    {
        var at = At;
        Label(at, caps, color);
        Reserve(at, Width, Txt.Natural(Face.Mono, 11));
    }

    /// <summary>Gold words that are a link (ink when pointed at); true when pressed.</summary>
    public static bool Link(string id, Vector2 pos, string text, int px = 13, Face face = Face.Sans)
    {
        var size = Txt.Measure(text, face, px);
        var (clicked, hovered, _) = Hit(id, pos, size);
        Txt.Draw(Dl, pos, text, face, px, hovered ? Palette.Ink : Palette.GoldText);
        return clicked;
    }

    /// <summary>A dot of a colour, 7px (or another size).</summary>
    public static void Dot(Vector2 center, Vector4 color, float sizeCss = 7) => Dl.AddCircleFilled(center, S(sizeCss) / 2, Palette.U32(color), 16);

    /// <summary>The site's gold coin dot after a gil amount: a small gold disc with a darker rim.</summary>
    public static float Coin(Vector2 pos, float lineH)
    {
        var d = S(7);
        var c = new Vector2(pos.X + S(3) + d / 2, pos.Y + lineH / 2);
        Dl.AddCircleFilled(c, d / 2, Palette.U32(Palette.Gold), 12);
        Dl.AddCircle(c, d / 2 - 0.5f, Palette.U32(Palette.GoldDeep), 12, 1f);
        return S(3) + d;
    }
}
