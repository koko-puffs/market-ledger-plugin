using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using MarketLedger.Core;
using MarketLedger.Session;
using static MarketLedger.UI.Theme;

namespace MarketLedger.UI;

/// <summary>
/// Run: the run in hand in full. On the left the steps as a table (#, the step, which plugin does
/// it, its progress, its state; the one under way in gold) and the log, each helper's name in its
/// tint; on the right what it's doing now, the numbers (elapsed, left, free slots, gil, spent of
/// what was agreed), the switches that matter while it runs, and the way to the site. Narrow, the
/// right column goes under the left.
/// </summary>
public sealed partial class MainWindow
{
    private readonly List<Memo<long>> stepCounts = new();
    private readonly Dictionary<long, string> logTimes = new();
    private readonly Dictionary<string, string> logWho = new();
    private readonly Dictionary<string, string> runLines = new();
    private readonly Memo<long> nowSlots = new();
    private readonly Memo<long> nowGil = new();
    private readonly Memo<long> nowSpent = new();
    private readonly Memo<long> nowMax = new();
    private readonly Memo<int> nowPercent = new();
    private readonly Memo<int> keepSlotsWords = new();

    private void DrawRunTab()
    {
        var run = session.Current;
        Frame.BeginBody("##run", null, 18, 24);
        if (run == null)
        {
            DrawNoRun();
            Frame.EndBody();
            return;
        }

        // The run's name and a line about it, the buttons under them.
        var at = Kit.At;
        var w = Kit.Width;
        var titleH = Title(at, run.File.Name, w, 18, 1);
        var line = RunSummary(run);
        Txt.Line(Kit.Dl, new Vector2(at.X, at.Y + titleH + S(2)), Txt.Fit(line, Face.Sans, 12, w), Face.Sans, 12, Palette.Muted, 18);
        Kit.Reserve(at, w, titleH + S(2) + S(18));
        Kit.Gap(10);
        RunButtons(1, run, false);
        Kit.Gap(16);

        var wide = w >= S(700);
        if (wide) Kit.BeginColumns(S(250), 14);
        DrawSteps(run);
        Kit.Gap(14);
        DrawLog(run, wide);
        if (wide) Kit.NextColumn();
        else Kit.Gap(14);
        DrawNow(run);
        Kit.Gap(10);
        DrawNumbers(run);
        Kit.Gap(10);
        DrawWhileRunning();
        Kit.Gap(12);
        var open = Kit.At;
        if (Kit.Button("##run-site", open, "Open on kokos-tools.com", Btn.Primary, BtnSize.Sm, Icon.ExternalLink, Kit.Width))
            Dalamud.Utility.Util.OpenLink(Ui.RunUrl(run.File.Id));
        Kit.Reserve(open, Kit.Width, Kit.ButtonHeight(BtnSize.Sm));
        if (wide) Kit.EndColumns();
        Frame.EndBody();
    }

    /// <summary>"7 steps · up to 120,000 gil · about 40 min · makes about 230,000", once per run.</summary>
    private string RunSummary(RunState run)
    {
        var id = run.File.Id ?? string.Empty;
        if (runLines.TryGetValue(id, out var line)) return line;
        if (runLines.Count > 32) runLines.Clear();
        var max = (long)run.File.MaxGil;
        var minutes = (double)run.File.Estimate.Minutes;
        var profit = (long)run.File.Estimate.Profit;
        line = Ui.Plural(run.Steps.Count, "step", "steps")
               + (max > 0 ? $" · up to {Ui.Gil(max)} gil" : string.Empty)
               + (minutes > 0 ? $" · {Ui.About(minutes)}" : string.Empty)
               + (profit != 0 ? $" · {(profit > 0 ? "+" : "−")}{Ui.Gil(Math.Abs(profit))} gil, about" : string.Empty);
        return runLines[id] = line;
    }

    // ---------------------------------------------------------------------------------------------
    // The steps.

    private void DrawSteps(RunState run)
    {
        var steps = run.Steps;
        while (stepCounts.Count < steps.Count) stepCounts.Add(new Memo<long>());

        Kit.BeginPanel("##run-steps", 0, 0, 0, Palette.Card, Palette.Line, Palette.RadiusMd);
        var dl = Kit.Dl;
        var w = Kit.Width;
        var pad = S(12);
        var gap = S(10);
        var numW = S(22);
        var stateW = S(64);
        var progW = MathF.Min(S(150), MathF.Max(S(90), w * 0.24f));
        var byW = w >= S(520) ? S(124) : 0;
        var stepW = w - pad * 2 - numW - stateW - progW - byW - gap * (byW > 0 ? 4 : 3);
        var xNum = Kit.Left + pad;
        var xStep = xNum + numW + gap;
        var xBy = xStep + stepW + gap;
        var xProg = xBy + (byW > 0 ? byW + gap : 0);
        var xState = xProg + progW + gap;

        // The head.
        var head = Kit.At;
        var headH = S(30);
        Kit.RowFill(head, head + new Vector2(w, headH), Palette.Raised, true, steps.Count == 0);
        var ly = head.Y + (headH - Txt.Natural(Face.Mono, 11)) / 2;
        Kit.Label(new Vector2(xNum, ly), "#");
        Kit.Label(new Vector2(xStep, ly), "STEP");
        if (byW > 0) Kit.Label(new Vector2(xBy, ly), "DONE BY");
        Kit.Label(new Vector2(xProg, ly), "PROGRESS");
        Kit.Label(new Vector2(xState, ly), "STATE");
        Kit.Reserve(head, w, headH);

        var rowH = S(36);
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            var at = Kit.At;
            var last = i == steps.Count - 1;
            var now = i == run.StepIndex && Ui.Going(run.Status);
            if (now)
            {
                Kit.RowFill(at, at + new Vector2(w, rowH), Palette.GoldRow, false, last);
                dl.AddRect(new Vector2(Kit.Left + 1, at.Y), new Vector2(Kit.Left + w - 1, at.Y + rowH), Palette.U32(Palette.GoldEdge),
                    last ? S(Palette.RadiusMd) - 1 : 0, last ? ImDrawFlags.RoundCornersBottom : ImDrawFlags.None, 1f);
            }
            else if (i % 2 == 1) Kit.RowFill(at, at + new Vector2(w, rowH), Palette.RowStripe, false, last);

            Txt.Line(dl, new Vector2(xNum, at.Y), Number(i + 1), Face.Mono, 12, now ? Palette.Gold : Palette.Muted, 36);
            var ic = Ui.KindIcon(s.Kind);
            var iconPx = S(14);
            Icons.Draw(dl, ic, new Vector2(xStep, at.Y + (rowH - iconPx) / 2), iconPx, now ? Palette.Gold : Palette.Faint);
            var labelX = xStep + iconPx + S(8);
            var label = s.Label ?? string.Empty;
            Txt.Line(dl, new Vector2(labelX, at.Y), Txt.Fit(label, Face.Sans, 13, xStep + stepW - labelX), Face.Sans, 13,
                now ? Palette.White : s.State is StepState.Done or StepState.Skipped ? Palette.Soft : Palette.Ink, 36);
            if (byW > 0 && !string.IsNullOrEmpty(s.By))
                Kit.Chip(new Vector2(xBy, at.Y + (rowH - S(22)) / 2), s.By, ChipKind.Quiet, false, 22, byW);

            StepBar(new Vector2(xProg, at.Y + (rowH - S(16)) / 2), progW, s, stepCounts[i]);
            Txt.Line(dl, new Vector2(xState, at.Y), Ui.StateWord(s.State), Face.Sans, 12, now ? Palette.GoldSoft : Ui.StateColor(s.State), 36);

            // The step's note (why it failed, when a timed node opens) and its whole name, when pointed at.
            if (!Kit.Blocked && ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(at, at + new Vector2(w, rowH)))
            {
                var tip = string.IsNullOrEmpty(s.Note) ? label : s.Note;
                if (tip.Length > 0) Kit.Tooltip(tip);
            }
            Kit.Reserve(at, w, rowH);
        }
        Kit.EndPanel();
    }

    /// <summary>A step's bar, 16 tall: its count in the middle in mono ("112 / 280"), or its note ("in 4 min") while it waits.</summary>
    private static void StepBar(Vector2 pos, float width, StepProgress s, Memo<long> memo)
    {
        var dl = Kit.Dl;
        var h = S(16);
        var fraction = s.State is StepState.Done ? 1f : s.Of > 0 ? Math.Clamp((float)s.Done / s.Of, 0, 1) : 0;
        var fill = s.State == StepState.Failed ? Palette.Loss : Palette.GoldDeep;
        dl.AddRectFilled(pos, pos + new Vector2(width, h), Palette.U32(Palette.Track), S(4));
        if (fraction > 0) dl.AddRectFilled(pos, pos + new Vector2(MathF.Max(S(4), width * fraction), h), Palette.U32(fill), S(4));
        string text;
        if (s.Of <= 0) text = string.IsNullOrEmpty(s.Note) ? "—" : s.Note;
        else if (s.State == StepState.Waiting && s.Done == 0 && !string.IsNullOrEmpty(s.Note)) text = s.Note;
        else text = memo.Get(((long)s.Done << 32) | (uint)s.Of, static k => $"{Ui.Count((int)(k >> 32))} / {Ui.Count((int)(k & 0xFFFFFFFF))}");
        text = Txt.Fit(text, Face.Mono, 11, width - S(8));
        var tw = Txt.Measure(text, Face.Mono, 11).X;
        Txt.Line(dl, new Vector2(pos.X + (width - tw) / 2, pos.Y), text, Face.Mono, 11, fraction > 0 ? Palette.White : Palette.Muted, 16);
    }

    private static readonly string[] Numbers = MakeNumbers(99);

    private static string[] MakeNumbers(int n)
    {
        var a = new string[n + 1];
        for (var i = 0; i <= n; i++) a[i] = i.ToString(CultureInfo.InvariantCulture);
        return a;
    }

    private static string Number(int n) => n >= 0 && n < Numbers.Length ? Numbers[n] : n.ToString(CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------------------------------------
    // The log.

    private void DrawLog(RunState run, bool wide)
    {
        var log = run.Log;
        Kit.Section("Log", log.Count == 0 ? null : "newest first");
        Kit.Gap(8);
        var height = wide ? MathF.Max(S(170), Frame.RoomLeft()) : S(240);
        Kit.BeginScrollCard("##run-log", height, 12, Palette.Card, Palette.Line, Palette.RadiusMd);
        if (log.Count == 0) Lines.Para("Nothing yet: each thing a helper does is written here as it happens.", Palette.Faint, 12, 18);
        if (logTimes.Count > 400) logTimes.Clear();
        var dl = Kit.Dl;
        var timeW = Txt.Measure("00:00:00", Face.Mono, 12).X;
        for (var i = 0; i < log.Count; i++)
        {
            var l = log[i];
            var at = Kit.At;
            var w = Kit.Width;
            var who = LogWho(l.Who);
            var textX = at.X + timeW + S(8) + Txt.Measure(who, Face.Mono, 12).X + S(8);
            var textW = MathF.Max(S(60), at.X + w - textX);
            var text = l.Text ?? string.Empty;
            var h = MathF.Max(S(18), Lines.Height(text, textW, Face.Mono, 12, 18));
            // Up to 200 lines: only those in the card's view are drawn, the rest only take their room.
            if (ImGui.IsRectVisible(at, at + new Vector2(w, h)))
            {
                Txt.Line(dl, at, LogTime(l.At), Face.Mono, 12, Palette.Dim, 18);
                Txt.Line(dl, new Vector2(at.X + timeW + S(8), at.Y), who, Face.Mono, 12, Ui.Tint(l.Who), 18);
                Lines.Draw(dl, new Vector2(textX, at.Y), textW, text, Face.Mono, 12, Palette.Soft, 18);
            }
            Kit.Reserve(at, w, h);
            Kit.Gap(3);
        }
        Kit.EndScrollCard();
    }

    private string LogTime(DateTime at)
    {
        if (logTimes.TryGetValue(at.Ticks, out var s)) return s;
        var local = at.Kind == DateTimeKind.Local ? at : DateTime.SpecifyKind(at, DateTimeKind.Utc).ToLocalTime();
        return logTimes[at.Ticks] = local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>"[GBR]", "[Artisan]": the helper's name in brackets, GatherBuddy Reborn shortened as the mockup has it.</summary>
    private string LogWho(string? who)
    {
        who ??= "Market Ledger";
        if (logWho.TryGetValue(who, out var s)) return s;
        var shown = who.StartsWith("GatherBuddy", StringComparison.OrdinalIgnoreCase) ? "GBR" : who;
        return logWho[who] = "[" + shown + "]";
    }

    // ---------------------------------------------------------------------------------------------
    // Now, the numbers, while running.

    private void DrawNow(RunState run)
    {
        var step = Ui.StepNow(run);
        var live = Ui.Going(run.Status);
        Kit.BeginPanel("##run-now", 14, 14, 14, live ? Palette.GoldWash : Palette.Card, live ? Palette.GoldEdge : Palette.Line, Palette.RadiusMd);
        Kit.LabelRow("NOW", live ? Palette.GoldText : Palette.Muted);
        Kit.Gap(6);
        var at = Kit.At;
        var title = step?.Label ?? Ui.StatusWord(run.Status);
        var th = Title(at, title, Kit.Width, 15, 2);
        Kit.Reserve(at, Kit.Width, th);
        if (!string.IsNullOrEmpty(run.Doing))
        {
            Kit.Gap(4);
            Lines.Para(run.Doing, Palette.Soft, 12, 18);
        }
        if (!string.IsNullOrEmpty(run.Message))
        {
            Kit.Gap(6);
            RunMessage("##now-ended", run, 12, 18);
        }
        if (run.HeldForYou && Ui.Going(run.Status))
        {
            Kit.Gap(10);
            var b = Kit.At;
            if (Kit.Button("##now-continue", b, "Continue", Btn.Primary, BtnSize.Sm, Icon.Play, Kit.Width, false,
                    "Lets the step it's holding go ahead"))
                Later(session.Resume);
            Kit.Reserve(b, Kit.Width, Kit.ButtonHeight(BtnSize.Sm));
        }
        if (!string.IsNullOrEmpty(run.CopyText))
        {
            Kit.Gap(8);
            var b = Kit.At;
            var copy = run.CopyText;
            if (Kit.Button("##now-copy", b, "Copy the list", Btn.Glass, BtnSize.Sm, Icon.Clipboard, Kit.Width, false,
                    "Copies what this step needs you to paste (GatherBuddy Reborn: its Auto-Gather tab, Import from TeamCraft)"))
            {
                ImGui.SetClipboardText(copy);
                Notify("Copied");
            }
            Kit.Reserve(b, Kit.Width, Kit.ButtonHeight(BtnSize.Sm));
        }
        if (step is { } s && s.Of > 0)
        {
            Kit.Gap(10);
            var p = Kit.At;
            var w = Kit.Width;
            var f = s.State == StepState.Done ? 1f : Math.Clamp((float)s.Done / s.Of, 0, 1);
            Kit.Progress(p, w, f, 16, Palette.GoldDeep);
            var pct = nowPercent.Get((int)MathF.Round(f * 100), static v => v.ToString(CultureInfo.InvariantCulture) + "%");
            var tw = Txt.Measure(pct, Face.Mono, 11).X;
            Txt.Line(Kit.Dl, new Vector2(p.X + (w - tw) / 2, p.Y), pct, Face.Mono, 11, Palette.White, 16);
            Kit.Reserve(p, w, S(16));
        }
        Kit.EndPanel();
    }

    private void DrawNumbers(RunState run)
    {
        Kit.BeginPanel("##run-numbers", 8, 14, 10, Palette.Card, Palette.Line, Palette.RadiusMd);
        NumberRow("Elapsed", ElapsedText(run));
        NumberRow("Left, about", LeftText(run));
        var who = session.Character;
        if (who?.FreeSlots is { } slots)
            NumberRow("Free bag slots", nowSlots.Get(slots, static v => Ui.Count((int)v)), slots < config.KeepFreeSlots ? Palette.Loss : null);
        if (who?.Gil is { } gil) NumberRow("Gil now", nowGil.Get(gil, static v => Ui.Gil(v)));
        var max = (long)run.File.MaxGil;
        NumberRow("Spent this run", nowSpent.Get(run.Spent, static v => v > 0 ? "−" + Ui.Gil(v) : "0"), run.Spent > 0 ? Palette.Loss : null);
        if (max > 0)
        {
            NumberRow("Agreed on the site", nowMax.Get(max, static v => Ui.Gil(v)), Palette.Muted);
            Kit.Gap(6);
            var p = Kit.At;
            Kit.Progress(p, Kit.Width, (float)run.Spent / max, 4, run.Spent > max ? Palette.Loss : Palette.Gold);
            Kit.Reserve(p, Kit.Width, S(4));
        }
        Kit.EndPanel();
    }

    private void DrawWhileRunning()
    {
        Kit.BeginPanel("##run-while", 12, 14, 12, Palette.Card, Palette.Line, Palette.RadiusMd);
        Kit.LabelRow("WHILE RUNNING");
        Kit.Gap(8);
        if (TickRow("##while-tell", "Pause on a tell", config.PauseOnTell))
        {
            config.PauseOnTell = !config.PauseOnTell;
            config.Save();
        }
        var slotsWords = keepSlotsWords.Get(config.KeepFreeSlots, static n => n > 0 ? $"Pause under {n} free slots" : "Never pause for bag space");
        if (TickRow("##while-slots", slotsWords, config.KeepFreeSlots > 0, "Change how many in Settings"))
            tab = MainTab.Settings;
        if (TickRow("##while-trust", "Trust the new clicks", config.TrustClicks,
                "On, clicks not yet seen working in the game go ahead by themselves. Off, the run holds before each until you press Continue."))
        {
            config.TrustClicks = !config.TrustClicks;
            config.Save();
        }
        if (TickRow("##while-site", "Let the site pause and stop", config.LetSiteControl,
                "The run's page on kokos-tools.com can pause, resume, skip and stop it"))
        {
            config.LetSiteControl = !config.LetSiteControl;
            config.Save();
        }
        if (TickRow("##while-ar", "Hold AutoRetainer until done", config.HoldAutoRetainer,
                "AutoRetainer's own runs wait while this one goes"))
        {
            config.HoldAutoRetainer = !config.HoldAutoRetainer;
            config.Save();
        }
        Kit.EndPanel();
    }

    /// <summary>A tick box with its words, the whole row pressable; true when pressed.</summary>
    private static bool TickRow(string id, string label, bool on, string? tip = null)
    {
        var at = Kit.At;
        var w = Kit.Width;
        var h = S(26);
        var (clicked, hovered, _) = Kit.Hit(id, at, new Vector2(w, h));
        Kit.TickBox(new Vector2(at.X, at.Y + (h - S(18)) / 2), on, hovered);
        Txt.Line(Kit.Dl, new Vector2(at.X + S(18 + 8), at.Y), Txt.Fit(label, Face.Sans, 12, w - S(26)), Face.Sans, 12, on ? Palette.Soft : Palette.Muted, 26);
        if (tip != null && hovered) Kit.Tooltip(tip);
        Kit.FocusRing(at, at + new Vector2(w, h), S(6));
        Kit.Reserve(at, w, h);
        return clicked;
    }
}
