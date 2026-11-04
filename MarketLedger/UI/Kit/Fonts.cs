using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Game;
using Dalamud.Interface.ManagedFontAtlas;

namespace MarketLedger.UI;

/// <summary>The site's faces, by weight: Geist, as Market Ledger uses it.</summary>
public enum Face
{
    /// <summary>Geist 400: what you read.</summary>
    Sans,

    /// <summary>Geist 500: buttons, names in lists.</summary>
    SansMedium,

    /// <summary>Geist 600: headings (tight, as the site's).</summary>
    SansSemibold,

    /// <summary>Geist Mono 400: every number (gil, counts, times), so they line up.</summary>
    Mono,
}

/// <summary>
/// The faces at the sizes the window uses, loaded through Dalamud's font atlas (so they follow
/// Dalamud's global scale and rebuild with it): Geist 15/14/13/12 (400), 15/14/13/12 (500), 30/22/18/15/14/13
/// (600); Geist Mono 22/15/13/12/11 (every number: gil, counts, times). Under the SIL Open Font License 1.1, unmodified, with its licence
/// (Resources/Fonts).
///
/// Sizes are CSS sizes (the em). ImGui sizes a font by its line (ascent to descent), so each face is
/// loaded at its em times its line ratio, and 14px Geist here is as tall as 14px in the browser.
///
/// On a Japanese client the sans sizes are Dalamud's own font (it has the kana and kanji that duty
/// and ability names need); the mono keeps the design's face.
/// </summary>
public sealed class Fonts : IDisposable
{
    /// <summary>(hhea ascent − descent) / em, read from the font files.</summary>
    private const float GeistLine = 1.3f;

    private static readonly (Face Face, int Px)[] Wanted =
    {
        (Face.Sans, 14), (Face.Sans, 13), (Face.Sans, 12),
        (Face.SansMedium, 14), (Face.SansMedium, 13), (Face.SansMedium, 12),
        (Face.SansSemibold, 30), (Face.SansSemibold, 22), (Face.SansSemibold, 18), (Face.SansSemibold, 15), (Face.SansSemibold, 14), (Face.SansSemibold, 13),
        (Face.Mono, 22), (Face.Mono, 15), (Face.Mono, 13), (Face.Mono, 12), (Face.Mono, 11),
    };

    private readonly Dictionary<(Face, int), IFontHandle> handles = new();

    public Fonts()
    {
        var atlas = Services.PluginInterface.UiBuilder.FontAtlas;
        var japanese = Services.ClientState.ClientLanguage == ClientLanguage.Japanese;
        foreach (var (face, px) in Wanted)
        {
            var file = face switch
            {
                Face.Sans => "Geist-Regular.ttf",
                Face.SansMedium => "Geist-Medium.ttf",
                Face.SansSemibold => "Geist-SemiBold.ttf",
                _ => "GeistMono-Regular.ttf",
            };
            var dalamudFont = japanese && face is not Face.Mono;
            // The mono only shows times and counts: it needs no other language's letters.
            handles[(face, px)] = Make(atlas, file, px * GeistLine, dalamudFont, face is not Face.Mono);
        }
    }

    /// <summary>Pushes a face at a CSS size (the nearest loaded size when that one isn't).</summary>
    public IDisposable Push(Face face, int px) => Handle(face, px).Push();

    public IFontHandle Handle(Face face, int px)
    {
        if (handles.TryGetValue((face, px), out var h)) return h;
        var best = handles.Keys.Where(k => k.Item1 == face).OrderBy(k => Math.Abs(k.Item2 - px)).FirstOrDefault();
        return handles.TryGetValue(best, out h) ? h : handles[(Face.Sans, 14)];
    }

    private static IFontHandle Make(IFontAtlas atlas, string file, float sizePx, bool dalamudFont, bool languages) =>
        atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
        {
            var font = dalamudFont
                ? tk.AddDalamudDefaultFont(sizePx, null)
                : tk.AddFontFromMemory(Read(file), new SafeFontConfig { SizePx = sizePx }, file);
            // The game's own symbols and whatever Dalamud's language needs (ability names in any client's language).
            tk.AddGameSymbol(new SafeFontConfig { SizePx = sizePx, MergeFont = font });
            if (languages) tk.AttachExtraGlyphsForDalamudLanguage(new SafeFontConfig { SizePx = sizePx, MergeFont = font });
        }));

    private static readonly Dictionary<string, byte[]> Files = new();

    private static byte[] Read(string file)
    {
        lock (Files)
        {
            if (Files.TryGetValue(file, out var cached)) return cached;
            var asm = typeof(Fonts).Assembly;
            var name = asm.GetManifestResourceNames().First(n => n.EndsWith(file, StringComparison.Ordinal));
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return Files[file] = ms.ToArray();
        }
    }

    public void Dispose()
    {
        foreach (var h in handles.Values) h.Dispose();
        handles.Clear();
    }
}
