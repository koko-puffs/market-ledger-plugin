using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace MarketLedger.UI;

/// <summary>
/// Market Ledger's colours, as the site has them (the design language in the site's
/// src/features/market-ledger styles): a black ground, near-black boxes with hairline edges, grey
/// words, and one gil gold for what goes ahead, what's live and what's yours; green for gains and
/// red for losses, always with a sign or an arrow beside them.
/// </summary>
public static class Palette
{
    // Surfaces.
    public static readonly Vector4 Ground = Hex(0x000000);                 // the window
    public static readonly Vector4 Footer = Hex(0x0a0a0b);                 // the title bar, the window's foot
    public static readonly Vector4 Card = Hex(0x111113);                   // boxes
    public static readonly Vector4 Raised = Hex(0x141416);                 // a box inside a box, table heads
    public static readonly Vector4 Float = Hex(0x0d0d0e);                  // menus, tooltips, toasts
    public static readonly Vector4 Field = Hex(0x141416);                  // fields
    public static readonly Vector4 Tile = Hex(0x18181b);                   // icon wells, the quiet tag
    public static readonly Vector4 ButtonFill = Rgba(0, 0, 0, 0);          // an outlined button's inside
    public static readonly Vector4 Picked = Rgba(255, 255, 255, 0.12f);   // the picked one in a pick-one pill

    // Words, brightest to quietest.
    public static readonly Vector4 White = Hex(0xffffff);
    public static readonly Vector4 Ink = Hex(0xf5f5f6);
    public static readonly Vector4 Soft = Hex(0xd4d4d8);                   // words in boxes
    public static readonly Vector4 Light = Hex(0xb9b9bf);                  // quiet buttons, links
    public static readonly Vector4 Muted = Hex(0x8e8e95);                  // labels
    public static readonly Vector4 Dim = Hex(0x76767e);                    // small mono labels, times in the log
    public static readonly Vector4 Faint = Hex(0x5e5e66);                  // what hardly needs reading

    // Gil gold: what goes ahead, what's live, the tool's own icons.
    public static readonly Vector4 Gold = Hex(0xffc53d);
    public static readonly Vector4 GoldHover = Hex(0xffd060);
    public static readonly Vector4 GoldSoft = Hex(0xffd978);               // gold words on the dark
    public static readonly Vector4 GoldText = Hex(0xffd978);               // gold notes
    public static readonly Vector4 GoldDeep = Hex(0xc9961f);               // a progress bar's fill inside a table
    public static readonly Vector4 GoldDim = Hex(0x8a6a1e);                // the step strip's step under way, not lit
    public static readonly Vector4 OnGold = Hex(0x1a1300);                 // ink on the gold
    public static readonly Vector4 GoldWash = Hex(0x141005);               // a live box's fill (the gold at 7% on black)
    public static readonly Vector4 GoldTile = Hex(0x2a2412);               // the round wells behind its icons
    public static readonly Vector4 GoldPicked = Rgba(255, 197, 61, 0.16f);
    public static readonly Vector4 GoldRow = Rgba(255, 197, 61, 0.07f);    // the step under way in the table
    public static readonly Vector4 GoldEdge = Hex(0x5a4516);               // a live box's edge (the gold at 35% on black)

    // Gains and losses.
    public static readonly Vector4 Gain = Hex(0x5ee6a8);
    public static readonly Vector4 Loss = Hex(0xff8a8a);

    // States.
    public static readonly Vector4 Danger = Hex(0xff8a8a);
    public static readonly Vector4 DangerSolid = Hex(0xff4f4f);
    public static readonly Vector4 DangerWash = Rgba(255, 79, 79, 0.1f);
    public static readonly Vector4 DangerEdge = Rgba(255, 79, 79, 0.45f);
    public static readonly Vector4 Success = Hex(0x5ee6a8);
    public static readonly Vector4 SuccessWash = Rgba(94, 230, 168, 0.1f);
    public static readonly Vector4 SuccessEdge = Rgba(94, 230, 168, 0.4f);
    public static readonly Vector4 Warn = Hex(0xffd978);                   // "partly": gold words, not red

    // Lines.
    public static readonly Vector4 LineFaint = Rgba(255, 255, 255, 0.06f); // dividers in boxes, hover fills
    public static readonly Vector4 LinePicked = Rgba(255, 255, 255, 0.09f);
    public static readonly Vector4 Line = Hex(0x1f1f22);                   // boxes
    public static readonly Vector4 LineStrong = Hex(0x34343a);             // things you press or type in
    public static readonly Vector4 LineHover = Hex(0x4a4a52);              // any edge pointed at
    public static readonly Vector4 LineDashed = Hex(0x34343a);             // things you can add
    public static readonly Vector4 RowHover = Rgba(255, 255, 255, 0.04f);
    public static readonly Vector4 RowStripe = Rgba(255, 255, 255, 0.025f); // every other row of a table
    public static readonly Vector4 Track = Hex(0x26262b);                  // a progress bar's track

    // Radii (CSS px, before Dalamud's global scale): the site's corners.
    public const float RadiusSm = 8;     // icon tiles, tick boxes
    public const float RadiusMd = 12;    // fields, menu rows, list rows
    public const float RadiusLg = 16;    // boxes, menus
    public const float RadiusXl = 22;    // windows, the question's card
    public const float Radius2Xl = 22;
    public const float RadiusCard = 16;
    public const float RadiusPill = 999; // buttons, pick-one pills, switches, chips

    /// <summary>Everything drawn is multiplied by this (0.5 for a disabled button, 0.4 for a small control).</summary>
    public static float Opacity { get; set; } = 1f;

    public static Vector4 Hex(uint rgb, float alpha = 1f) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);

    public static Vector4 Rgba(int r, int g, int b, float a) => new(r / 255f, g / 255f, b / 255f, a);

    /// <summary>A translucent colour flattened over an opaque one.</summary>
    public static Vector4 Over(Vector4 top, Vector4 under) =>
        new(under.X + (top.X - under.X) * top.W, under.Y + (top.Y - under.Y) * top.W, under.Z + (top.Z - under.Z) * top.W, 1f);

    public static Vector4 WithAlpha(Vector4 c, float a) => new(c.X, c.Y, c.Z, a);

    public static uint U32(Vector4 c) => ImGui.ColorConvertFloat4ToU32(new Vector4(c.X, c.Y, c.Z, c.W * Opacity));
}
