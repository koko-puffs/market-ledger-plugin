using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using static MarketLedger.UI.Theme;

namespace MarketLedger.UI;

/// <summary>A piece of styled text in a paragraph: words in a face, size and colour, or a dye swatch, or a link.</summary>
public readonly record struct Run(string Text, Face Face, int Px, Vector4 Color, string? Link = null, uint? Swatch = null)
{
    public static Run Note(string text, Vector4? color = null) => new(text, Face.Sans, 13, color ?? Palette.Muted);

    public static Run Dot(uint? rgb) => new(string.Empty, Face.Sans, 13, Palette.Muted, Swatch: rgb ?? 0xFFFFFFFF);
}

/// <summary>
/// Text as the browser lays it out: line boxes of a CSS line height with the glyphs centred in them
/// (half-leading), letter-spacing drawn glyph by glyph, ellipses, and paragraphs of mixed runs
/// wrapped at word boundaries with clickable links.
/// </summary>
public static class Txt
{
    public static Fonts Fonts { get; set; } = null!;

    public static Vector2 Measure(string text, Face face, int px)
    {
        using (Fonts.Push(face, px)) return ImGui.CalcTextSize(text);
    }

    /// <summary>The font's own line (ImGui's font size): CSS "line-height: normal".</summary>
    public static float Natural(Face face, int px)
    {
        using (Fonts.Push(face, px)) return ImGui.GetFontSize();
    }

    /// <summary>The ascent, for lining up baselines of different sizes.</summary>
    public static float Ascent(Face face, int px)
    {
        using (Fonts.Push(face, px)) return ImGui.GetFont().Ascent;
    }

    /// <summary>Draws text with its glyph box's top at <paramref name="pos"/>; returns its width.</summary>
    public static float Draw(ImDrawListPtr dl, Vector2 pos, string text, Face face, int px, Vector4 color)
    {
        using (Fonts.Push(face, px))
        {
            dl.AddText(Snap(pos), Palette.U32(color), text);
            return ImGui.CalcTextSize(text).X;
        }
    }

    /// <summary>Draws one line centred in a CSS line box <paramref name="lineCss"/> tall (0: the font's own line).</summary>
    public static float Line(ImDrawListPtr dl, Vector2 lineTop, string text, Face face, int px, Vector4 color, float lineCss = 0)
    {
        using (Fonts.Push(face, px))
        {
            var size = ImGui.GetFontSize();
            var box = lineCss > 0 ? S(lineCss) : size;
            dl.AddText(Snap(lineTop + new Vector2(0, (box - size) / 2)), Palette.U32(color), text);
            return ImGui.CalcTextSize(text).X;
        }
    }

    /// <summary>The height of a CSS line box in screen pixels.</summary>
    public static float LineBox(Face face, int px, float lineCss) => lineCss > 0 ? S(lineCss) : Natural(face, px);

    /// <summary>
    /// Letter-spaced text (CSS letter-spacing, which also follows the last letter): eyebrows at
    /// .18em, stamps at .1em. Returns the width.
    /// </summary>
    public static float Spaced(ImDrawListPtr dl, Vector2 pos, string text, Face face, int px, Vector4 color, float em, bool draw = true)
    {
        using (Fonts.Push(face, px))
        {
            var spacing = S(px * em);
            var x = pos.X;
            var col = Palette.U32(color);
            foreach (var ch in text)
            {
                var s = Glyph(ch);
                if (draw) dl.AddText(Snap(new Vector2(x, pos.Y)), col, s);
                x += ImGui.CalcTextSize(s).X + spacing;
            }
            return x - pos.X;
        }
    }

    private static readonly Dictionary<char, string> Glyphs = new();

    /// <summary>A letter as a string, made once (spaced labels are drawn letter by letter every frame).</summary>
    private static string Glyph(char ch)
    {
        if (Glyphs.TryGetValue(ch, out var s)) return s;
        return Glyphs[ch] = ch.ToString();
    }

    public static float SpacedWidth(string text, Face face, int px, float em) =>
        Spaced(default, Vector2.Zero, text, face, px, Vector4.Zero, em, draw: false);

    private static readonly Dictionary<(string Text, int Width, Face Face, int Px), string> Cut = new();
    private static int cutFor = -1;

    /// <summary>
    /// The text cut to fit a width, with an ellipsis. What's cut is kept (by text, width, face and
    /// size), so a long name in a narrow row isn't cut again, string by string, every frame.
    /// </summary>
    public static string Fit(string text, Face face, int px, float width)
    {
        using (Fonts.Push(face, px))
        {
            if (ImGui.CalcTextSize(text).X <= width) return text;
            if (!Fonts.Ready) return Shorten(text, width);
            if (cutFor != Fonts.Generation)
            {
                cutFor = Fonts.Generation;
                Cut.Clear();
            }
            var key = (text, (int)MathF.Floor(width), face, px);
            if (Cut.TryGetValue(key, out var cut)) return cut;
            if (Cut.Count > 400) Cut.Clear();
            return Cut[key] = Shorten(text, width);
        }
    }

    /// <summary>The longest start of the text that fits with "…" after it (the font already pushed).</summary>
    private static string Shorten(string text, float width)
    {
        const string dots = "…";
        var lo = 0;
        var hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (ImGui.CalcTextSize(text[..mid].TrimEnd() + dots).X <= width) lo = mid;
            else hi = mid - 1;
        }
        return lo == 0 ? dots : text[..lo].TrimEnd() + dots;
    }

    private readonly record struct Token(string Text, int Run, float Width, float Trailing);

    /// <summary>
    /// Lays out runs of text wrapped to <paramref name="width"/>, in line boxes <paramref name="lineCss"/>
    /// CSS pixels tall, from <paramref name="pos"/>. Links are underlined on hover and clickable;
    /// <paramref name="clicked"/> is the link pressed this frame. Leaves the cursor where it was.
    /// Returns the height.
    /// </summary>
    public static float Flow(Vector2 pos, float width, IReadOnlyList<Run> runs, float lineCss, out string? clicked, bool center = false, bool draw = true)
    {
        clicked = null;
        var tokens = new List<Token>();
        for (var r = 0; r < runs.Count; r++)
        {
            var run = runs[r];
            if (run.Swatch != null)
            {
                tokens.Add(new Token(string.Empty, r, S(10), S(6)));
                continue;
            }
            using (Fonts.Push(run.Face, run.Px))
            {
                var space = ImGui.CalcTextSize(" ").X;
                var text = run.Text;
                var i = 0;
                while (i < text.Length)
                {
                    var j = i;
                    while (j < text.Length && text[j] != ' ') j++;
                    var word = text[i..j];
                    var spaces = 0;
                    while (j < text.Length && text[j] == ' ')
                    {
                        spaces++;
                        j++;
                    }
                    tokens.Add(new Token(word, r, word.Length == 0 ? 0 : ImGui.CalcTextSize(word).X, spaces * space));
                    i = j;
                }
            }
        }

        // Break into lines.
        var lines = new List<(int From, int To, float Width)>();
        var from = 0;
        var x = 0f;
        var lineWidth = 0f;
        for (var t = 0; t < tokens.Count; t++)
        {
            var tok = tokens[t];
            if (t > from && x + tok.Width > width + 0.5f)
            {
                lines.Add((from, t, lineWidth));
                from = t;
                x = 0;
            }
            lineWidth = x + tok.Width;
            x += tok.Width + tok.Trailing;
        }
        if (from < tokens.Count) lines.Add((from, tokens.Count, lineWidth));

        var box = S(lineCss);
        if (!draw) return lines.Count * box;

        var dl = ImGui.GetWindowDrawList();
        var cursor = ImGui.GetCursorScreenPos();
        // The link under the mouse, found first: a link of several words lights up whole, whichever
        // of its words the mouse is on (each word is its own button, laid where it falls).
        string? hoveredLink = null;
        if (!Kit.Blocked && ImGui.IsWindowHovered())
        {
            var hy = pos.Y;
            foreach (var (a, b, w) in lines)
            {
                var hx = pos.X + (center ? (width - w) / 2 : 0);
                for (var t = a; t < b && hoveredLink == null; t++)
                {
                    var tok = tokens[t];
                    var link = runs[tok.Run].Link;
                    if (link != null && ImGui.IsMouseHoveringRect(new Vector2(hx, hy), new Vector2(hx + tok.Width + tok.Trailing, hy + box)))
                        hoveredLink = link;
                    hx += tok.Width + tok.Trailing;
                }
                if (hoveredLink != null) break;
                hy += box;
            }
        }
        var y = pos.Y;
        var linkIndex = 0;
        foreach (var (a, b, w) in lines)
        {
            var lx = pos.X + (center ? (width - w) / 2 : 0);
            for (var t = a; t < b; t++)
            {
                var tok = tokens[t];
                var run = runs[tok.Run];
                if (run.Swatch is { } rgb)
                {
                    Kit.Swatch(dl, new Vector2(lx + S(5), y + box / 2), rgb == 0xFFFFFFFF ? null : rgb);
                    lx += tok.Width + tok.Trailing;
                    continue;
                }
                using (Fonts.Push(run.Face, run.Px))
                {
                    var size = ImGui.GetFontSize();
                    var top = new Vector2(lx, y + (box - size) / 2);
                    var color = run.Color;
                    if (run.Link != null)
                    {
                        var min = new Vector2(lx, y);
                        var max = new Vector2(lx + tok.Width + (t < b - 1 && runs[tokens[t + 1].Run].Link == run.Link ? tok.Trailing : 0), y + box);
                        ImGui.SetCursorScreenPos(min);
                        var hit = ImGui.InvisibleButton(Ids.Of(LinkPrefix(run.Link), linkIndex++), Vector2.Max(max - min, new Vector2(1)));
                        if (hit && !Kit.Blocked) clicked = run.Link;
                        if (ImGui.IsItemHovered() && !Kit.Blocked) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                        if (run.Link == hoveredLink) color = Palette.Ink;
                    }
                    if (tok.Text.Length > 0) dl.AddText(Snap(top), Palette.U32(color), tok.Text);
                }
                lx += tok.Width + tok.Trailing;
            }
            y += box;
        }
        ImGui.SetCursorScreenPos(cursor);
        return lines.Count * box;
    }

    private static readonly Dictionary<string, string> LinkPrefixes = new();

    /// <summary>"##link" and the link, made once per link rather than each frame.</summary>
    private static string LinkPrefix(string link)
    {
        if (LinkPrefixes.TryGetValue(link, out var p)) return p;
        if (LinkPrefixes.Count > 64) LinkPrefixes.Clear();
        return LinkPrefixes[link] = "##link" + link;
    }

    /// <summary>A paragraph at the cursor across the box's width; the cursor moves below it.</summary>
    public static string? Para(IReadOnlyList<Run> runs, float lineCss = 19.5f, bool center = false, float? width = null)
    {
        var at = Kit.At;
        var w = width ?? Kit.Width;
        var h = Flow(at, w, runs, lineCss, out var clicked, center);
        Kit.Reserve(at, w, h);
        return clicked;
    }

    /// <summary>A note (13px ash, line-height 1.5) at the cursor.</summary>
    public static void Note(string text, Vector4? color = null, bool center = false) => Para(new[] { Run.Note(text, color) }, 19.5f, center);

    private static Vector2 Snap(Vector2 p) => new(MathF.Round(p.X), MathF.Round(p.Y));
}
