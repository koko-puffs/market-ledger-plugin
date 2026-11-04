using System;
using System.Collections.Generic;
using System.Numerics;
using MarketLedger.Core;
using MarketLedger.Session;
using static MarketLedger.UI.Theme;

namespace MarketLedger.UI;

/// <summary>
/// Helpers: the plugins Market Ledger drives, one row each: its name and whether the run needs it,
/// what Market Ledger asks of it, whether it's found (its version) and how its IPC answers (ok,
/// partly, not at all), with a note on the partly and the missing. Check again asks them all anew.
/// Under them, what goes to the site and how the market board is used.
/// </summary>
public sealed partial class MainWindow
{
    private readonly Dictionary<(string Name, string? Version, HelperIpc Ipc, bool Found), string> helperChips = new();

    private void DrawHelpersTab()
    {
        Frame.BeginBody("##helpers", null, 18, 24);

        Kit.BeginPanel("##helpers-intro", 16, 18, 16, Palette.GoldWash, Palette.GoldEdge);
        var at = Kit.At;
        var w = Kit.Width;
        const string again = "Check again";
        var bw = Kit.ButtonWidth(again, BtnSize.Tiny, Icon.Refresh);
        var tw = w - bw - S(16);
        var dl = Kit.Dl;
        var titleH = Lines.Draw(dl, at, tw, "Market Ledger plans and keeps order. Other plugins do the work.", Face.SansSemibold, 14, Palette.Ink, 20);
        var lineH = Lines.Draw(dl, new Vector2(at.X, at.Y + titleH + S(4)), tw,
            "It doesn't craft, gather or walk by itself: it asks the plugin that already does that well, waits for it, and checks the count in your bags. Buying it does itself, as Housing Studio does, never past the gil you agreed to on the site.",
            Face.Sans, 12, Palette.Muted, 18);
        var h = titleH + S(4) + lineH;
        if (Kit.Button("##helpers-again", new Vector2(at.X + w - bw, at.Y + MathF.Max(0, (h - Kit.ButtonHeight(BtnSize.Tiny)) / 2)), again, Btn.Glass,
                BtnSize.Tiny, Icon.Refresh, 0, false, "Looks for each plugin again and asks its IPC (after installing or updating one)"))
            Later(session.CheckHelpers);
        Kit.Reserve(at, w, MathF.Max(h, Kit.ButtonHeight(BtnSize.Tiny)));
        Kit.EndPanel();
        Kit.Gap(18);

        var helpers = session.Helpers;
        Kit.Section("The plugins it drives", helpers.Count == 0 ? null : FoundWords(helpers));
        Kit.Gap(10);
        if (helpers.Count == 0) Lines.Para("Looking for them…");
        else DrawHelperRows(helpers);

        Kit.Gap(22);
        Kit.Section("The site and the market");
        Kit.Gap(10);
        Kit.BeginPanel("##helpers-site", 14, 16, 14, Palette.Card, Palette.Line, Palette.RadiusMd);
        var linked = session.Link is LinkState.Linked l ? l.Account : null;
        var p = Kit.At;
        Kit.Dot(new Vector2(p.X + S(4), p.Y + S(10)), linked != null ? Palette.Gain : Palette.Faint, 8);
        Txt.Line(Kit.Dl, new Vector2(p.X + S(16), p.Y), Txt.Fit(linked != null ? LinkedAs(linked) : "Not linked to kokos-tools.com", Face.SansMedium, 13, Kit.Width - S(16)),
            Face.SansMedium, 13, Palette.Ink, 20);
        Kit.Reserve(p, Kit.Width, S(20));
        Kit.Gap(4);
        Lines.Para("It sends your jobs and levels, gil and free slots, what you hold, each run's progress and your retainers' sales and listings. Never your chat or anyone else's names.",
            Palette.Muted, 12, 18);
        Kit.Gap(10);
        Lines.Para("On the market board it buys at or under each line's price from the plan, checks every purchase question against it, and stops before the run's agreed gil. Clicks it hasn't been trusted with wait for Continue (Settings).",
            Palette.Muted, 12, 18);
        Kit.EndPanel();

        Frame.EndBody();
    }

    private readonly Memo<long> foundWords = new();

    /// <summary>"5 of 6 found".</summary>
    private string FoundWords(IReadOnlyList<HelperStatus> helpers)
    {
        var found = 0;
        for (var i = 0; i < helpers.Count; i++)
            if (helpers[i].Found) found++;
        return foundWords.Get(((long)found << 16) | (long)helpers.Count, static k => $"{k >> 16} of {k & 0xFFFF} found");
    }

    private string? linkedAsFor;
    private string linkedAs = string.Empty;

    private string LinkedAs(string account)
    {
        if (account == linkedAsFor) return linkedAs;
        linkedAsFor = account;
        return linkedAs = $"Linked as {account} · kokos-tools.com";
    }

    private void DrawHelperRows(IReadOnlyList<HelperStatus> helpers)
    {
        Kit.BeginPanel("##helpers-rows", 0, 0, 0, Palette.Card, Palette.Line, Palette.RadiusMd);
        var w = Kit.Width;
        var pad = S(16);
        var nameW = MathF.Min(S(170), w * 0.28f);
        var chipW = MathF.Min(S(190), w * 0.3f);
        var gap = S(14);
        var whatW = w - pad * 2 - nameW - chipW - gap * 2;
        var dl = Kit.Dl;

        var head = Kit.At;
        var headH = S(30);
        Kit.RowFill(head, head + new Vector2(w, headH), Palette.Raised, true, false);
        var ly = head.Y + (headH - Txt.Natural(Face.Mono, 11)) / 2;
        Kit.Label(new Vector2(head.X + pad, ly), "PLUGIN");
        Kit.Label(new Vector2(head.X + pad + nameW + gap, ly), "WHAT MARKET LEDGER ASKS OF IT");
        Kit.Label(new Vector2(head.X + w - pad - chipW, ly), "FOUND");
        Kit.Reserve(head, w, headH);

        for (var i = 0; i < helpers.Count; i++)
        {
            var hs = helpers[i];
            var at = Kit.At;
            if (i > 0) dl.AddLine(new Vector2(at.X + 1, at.Y - 0.5f), new Vector2(at.X + w - 1, at.Y - 0.5f), Palette.U32(Palette.Line), 1f);
            var x = at.X + pad;
            var top = at.Y + S(12);

            // The name, and needed or optional under it.
            var nameH = Txt.Natural(Face.SansSemibold, 13);
            Txt.Line(dl, new Vector2(x, top), Txt.Fit(hs.Name, Face.SansSemibold, 13, nameW), Face.SansSemibold, 13, Palette.Ink);
            Txt.Line(dl, new Vector2(x, top + nameH + S(2)), hs.Needed ? "Needed" : "Optional", Face.Sans, 12, hs.Needed ? Palette.GoldText : Palette.Muted, 18);
            var leftH = nameH + S(2) + S(18);

            // What it's asked, the IPC calls in mono, and the note under it.
            var wx = x + nameW + gap;
            var whatH = Lines.Draw(dl, new Vector2(wx, top), whatW, hs.What ?? string.Empty, Face.Sans, 13, Palette.Soft, 19);
            if (!string.IsNullOrEmpty(hs.Calls))
                whatH += S(2) + Lines.Draw(dl, new Vector2(wx, top + whatH + S(2)), whatW, hs.Calls, Face.Mono, 11, Palette.Dim, 16);
            if (!string.IsNullOrEmpty(hs.Note))
                whatH += S(2) + Lines.Draw(dl, new Vector2(wx, top + whatH + S(2)), whatW, hs.Note, Face.Sans, 12,
                    hs.Ipc == HelperIpc.Partly ? Palette.GoldText : Palette.Muted, 18);

            // Found, its version and how its IPC answers.
            var (text, kind) = HelperChip(hs);
            var cx = at.X + w - pad - chipW;
            Kit.Chip(new Vector2(cx, top), text, kind, kind == ChipKind.Ok, 24, chipW, Face.Mono);
            var rightH = S(24);
            // Not installed: the way to get it (its repository or page).
            if (!hs.Found && !string.IsNullOrEmpty(hs.Url))
            {
                const string getIt = "Get it";
                var url = hs.Url;
                if (Kit.Button(Ids.Of("##helper-get", i), new Vector2(cx, top + S(24) + S(8)), getIt, hs.Needed ? Btn.Primary : Btn.Glass, BtnSize.Tiny,
                        Icon.ExternalLink, 0, false, url))
                    Dalamud.Utility.Util.OpenLink(url);
                rightH += S(8) + Kit.ButtonHeight(BtnSize.Tiny);
            }

            Kit.Reserve(at, w, S(12) * 2 + MathF.Max(MathF.Max(leftH, whatH), rightH));
        }
        Kit.EndPanel();
    }

    /// <summary>"4.0.5.21 · IPC ok", "7.5.6.0 · partly", "not installed": the words made once per state.</summary>
    private (string Text, ChipKind Kind) HelperChip(HelperStatus hs)
    {
        var kind = !hs.Found ? hs.Needed ? ChipKind.Bad : ChipKind.Plain
            : hs.Ipc switch
            {
                HelperIpc.Ok => ChipKind.Ok,
                HelperIpc.Partly => ChipKind.Gold,
                _ => ChipKind.Bad,
            };
        var key = (hs.Name, hs.Version, hs.Ipc, hs.Found);
        if (helperChips.TryGetValue(key, out var text)) return (text, kind);
        if (helperChips.Count > 64) helperChips.Clear();
        var v = string.IsNullOrEmpty(hs.Version) ? null : hs.Version;
        text = !hs.Found ? "not installed"
            : hs.Ipc switch
            {
                HelperIpc.Ok => v != null ? $"{v} · IPC ok" : "IPC ok",
                HelperIpc.Partly => v != null ? $"{v} · partly" : "partly",
                _ => v != null ? $"{v} · no IPC" : "no IPC",
            };
        return (helperChips[key] = text, kind);
    }
}
