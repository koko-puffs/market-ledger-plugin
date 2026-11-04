using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using static MarketLedger.UI.Theme;

namespace MarketLedger.UI;

/// <summary>
/// The window, top to bottom: a header (20px from the top, 28px sides) that stays put, a
/// body that scrolls when it's tall, and a footer in the panel black pinned to the bottom, a hairline
/// above it. Inside the body, <see cref="BeginBottom"/> pushes what follows to the body's foot, as a
/// <c>flex:1</c> spacer does.
/// </summary>
public static class Frame
{
    private static readonly Dictionary<string, float> Heights = new();
    private static readonly Stack<(string Id, float Start, float PadBottom)> Footers = new();
    private static readonly Stack<(string Id, float Start)> Bottoms = new();
    private static float bodyPadBottom;

    /// <summary>At the start of each frame: nothing left over from a frame whose drawing threw part way.</summary>
    public static void ResetFrame()
    {
        Footers.Clear();
        Bottoms.Clear();
        Open.Clear();
    }

    /// <summary>The ImGui children and popups the kit has open now, innermost last (1.0.0).</summary>
    internal static readonly Stack<bool> Open = new();

    /// <summary>
    /// Ends what the kit left open (a view that threw between a Begin and its End), innermost first, so
    /// the window ends with ImGui's stack as it found it rather than with a child or popup still open
    /// (which ImGui asserts on). Nothing to do on a frame drawn to the end.
    /// </summary>
    public static void Unwind()
    {
        while (Open.Count > 0)
        {
            if (Open.Pop()) ImGui.EndPopup();
            else ImGui.EndChild();
        }
    }

    public const float SidePad = 28;

    /// <summary>Starts the header: the window's top-left plus its padding.</summary>
    public static void BeginHeader(float padTop)
    {
        var win = ImGui.GetWindowPos();
        var content = ImGui.GetCursorScreenPos();
        // The faint line under Dalamud's title bar, as the mockups' bar has.
        Kit.Dl.AddLine(new Vector2(win.X + 1, content.Y + 0.5f), new Vector2(win.X + ImGui.GetWindowWidth() - 1, content.Y + 0.5f), Palette.U32(Palette.Line), 1f);
        ImGui.SetCursorScreenPos(new Vector2(content.X, content.Y + S(padTop)));
        ImGui.Dummy(Vector2.Zero);
        ImGui.SetCursorScreenPos(new Vector2(content.X, content.Y + S(padTop)));
        Kit.PushBox(win.X + S(SidePad), ImGui.GetWindowWidth() - S(SidePad) * 2);
    }

    public static void EndHeader() => Kit.PopBox();

    /// <summary>
    /// Starts the scrolling body, as tall as what's left above the footer (its height from last
    /// frame). Padding in CSS pixels.
    /// </summary>
    public static void BeginBody(string id, string? footerId, float padTop, float padBottom)
    {
        var footer = footerId != null && Heights.TryGetValue(footerId, out var f) ? f : footerId != null ? S(110) : 0;
        var x = ImGui.GetWindowPos().X;
        var y = ImGui.GetCursorScreenPos().Y;
        ImGui.SetCursorScreenPos(new Vector2(x, y));
        var height = MathF.Max(S(40), ImGui.GetWindowPos().Y + ImGui.GetWindowHeight() - footer - y);
        ImGui.BeginChild(id, new Vector2(ImGui.GetWindowWidth(), height), false, ImGuiWindowFlags.None);
        Open.Push(false);
        var at = ImGui.GetCursorScreenPos();
        Kit.PushBox(at.X + S(SidePad), ImGui.GetContentRegionAvail().X - S(SidePad) * 2);
        Kit.Reserve(at, 1, S(padTop));
        bodyPadBottom = padBottom;
    }

    public static void EndBody()
    {
        Kit.Gap(bodyPadBottom);
        Kit.PopBox();
        if (Open.Count > 0) Open.Pop();
        ImGui.EndChild();
    }

    /// <summary>
    /// The mockups' <c>flex:1</c> spacer: a gap, then as much room as there is, then a gap, so what
    /// follows (until <see cref="EndBottom"/>) sits at the foot of the body. When the body is full,
    /// it's just the two gaps.
    /// </summary>
    public static void BeginBottom(string id, float gap = 18)
    {
        Kit.Gap(gap);
        var h = Heights.TryGetValue(id, out var v) ? v : 0;
        var bottom = ImGui.GetWindowPos().Y + ImGui.GetWindowHeight() - S(bodyPadBottom);
        var y = ImGui.GetCursorScreenPos().Y;
        var room = bottom - h - S(gap) - y;
        if (room > 0) Kit.Reserve(Kit.At, 1, room);
        Kit.Gap(gap);
        Bottoms.Push((id, ImGui.GetCursorScreenPos().Y));
    }

    /// <summary>
    /// The room left in the body below where you are, as if it weren't scrolled (so what fills it doesn't
    /// grow as you scroll), less the body's bottom padding and, given <paramref name="bottomId"/>, the
    /// <see cref="BeginBottom"/> part that comes after (its gaps and its height last frame).
    /// </summary>
    public static float RoomLeft(string? bottomId = null, float gap = 18)
    {
        var bottom = ImGui.GetWindowPos().Y + ImGui.GetWindowHeight() - S(bodyPadBottom);
        var y = ImGui.GetCursorScreenPos().Y + ImGui.GetScrollY();
        var after = bottomId != null ? S(gap) * 2 + (Heights.TryGetValue(bottomId, out var h) ? h : 0) : 0;
        return bottom - after - y;
    }

    public static void EndBottom()
    {
        var (id, start) = Bottoms.Pop();
        Heights[id] = ImGui.GetCursorScreenPos().Y - start;
    }

    /// <summary>Starts the walnut footer under the body, to the window's bottom edge.</summary>
    public static void BeginFooter(string id, float padTop, float padBottom)
    {
        var win = ImGui.GetWindowPos();
        var w = ImGui.GetWindowWidth();
        var top = ImGui.GetCursorScreenPos().Y;
        var bottom = win.Y + ImGui.GetWindowHeight();
        var dl = Kit.Dl;
        dl.AddRectFilled(new Vector2(win.X, top), new Vector2(win.X + w, bottom), Palette.U32(Palette.Footer), ImGui.GetStyle().WindowRounding, ImDrawFlags.RoundCornersBottom);
        dl.AddLine(new Vector2(win.X + 1, top + 0.5f), new Vector2(win.X + w - 1, top + 0.5f), Palette.U32(Palette.Line), 1f);
        Kit.PushBox(win.X + S(SidePad), w - S(SidePad) * 2);
        Kit.Reserve(new Vector2(win.X, top), 1, S(padTop));
        Footers.Push((id, top, padBottom));
        footerTop = top;
    }

    public static void EndFooter()
    {
        var (id, top, padBottom) = Footers.Pop();
        Heights[id] = ImGui.GetCursorScreenPos().Y + S(padBottom) - top;
        Kit.PopBox();
    }

    // ---------------------------------------------------------------------------------------------
    // Toasts: one line in the float material, over the footer, for a few seconds.

    private static string? toast;
    private static DateTime toastUntil;

    public static void Toast(string text)
    {
        toast = text;
        toastUntil = DateTime.UtcNow + TimeSpan.FromSeconds(2.6);
    }

    /// <summary>The top of the footer drawn this frame (0 when none): toasts sit above it, not over its buttons.</summary>
    private static float footerTop;

    public static void DrawToast()
    {
        var top = footerTop;
        footerTop = 0;
        if (toast == null) return;
        var left = (toastUntil - DateTime.UtcNow).TotalSeconds;
        if (left <= 0)
        {
            toast = null;
            return;
        }
        var fade = (float)Math.Clamp(left / 0.3, 0, 1);
        var old = Palette.Opacity;
        Palette.Opacity = ReducedMotion ? 1 : fade;
        var win = ImGui.GetWindowPos();
        // Within the window (a long message is cut short), above the footer.
        var text = Txt.Fit(toast, Face.Sans, 13, MathF.Max(S(80), ImGui.GetWindowWidth() - S(56) - S(16) * 2));
        var size = Txt.Measure(text, Face.Sans, 13);
        var box = new Vector2(size.X + S(16) * 2, S(36));
        var bottom = win.Y + ImGui.GetWindowHeight();
        var y = top > win.Y && top < bottom ? top - S(10) - box.Y : bottom - box.Y - S(28);
        var pos = new Vector2(win.X + (ImGui.GetWindowWidth() - box.X) / 2, y);
        var dl = ImGui.GetForegroundDrawList();
        // shadow-float, softly.
        for (var i = 3; i >= 1; i--)
            dl.AddRectFilled(pos + new Vector2(0, S(6 * i)), pos + box + new Vector2(0, S(6 * i)), Palette.U32(Palette.WithAlpha(Palette.Ground, 0.12f)), box.Y / 2 + S(4 * i));
        dl.AddRectFilled(pos, pos + box, Palette.U32(Palette.Over(Palette.Float, Palette.Ground)), box.Y / 2);
        dl.AddRect(pos, pos + box, Palette.U32(Palette.Line), box.Y / 2);
        using (Txt.Fonts.Push(Face.Sans, 13))
            dl.AddText(pos + new Vector2(S(16), (box.Y - ImGui.GetFontSize()) / 2), Palette.U32(Palette.Ink), text);
        Palette.Opacity = old;
    }
}
