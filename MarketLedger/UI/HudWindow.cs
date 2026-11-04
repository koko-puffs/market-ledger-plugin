using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using MarketLedger.Core;
using MarketLedger.Session;
using static MarketLedger.UI.Theme;

namespace MarketLedger.UI;

/// <summary>
/// The HUD: a small box of its own, no title bar, sized to what it holds, top right the first time
/// (then where you leave it). While a run is in hand: the step, a strip with a segment for each
/// step, the step's count, the time, Pause and the way to the main window; on the market, what it's
/// buying and the gil spent of what was agreed; Continue when a click is held for you. When a run
/// arrives from the site, it asks whether to start it now or after this one. It doesn't take focus
/// when it appears, doesn't close on Esc and makes no sound.
/// </summary>
public sealed class HudWindow : Window, IDisposable
{
    private const float Width = 380;
    private const float Pad = 14;

    private readonly LedgerSession session;
    private readonly Configuration config;
    private readonly Fonts fonts;
    private readonly List<Action> later = new();

    /// <summary>The runs whose question was answered "Not now" here (they stay waiting on Home).</summary>
    private readonly HashSet<Guid> notNow = new();

    private readonly Memo<long> headText = new();
    private readonly Memo<long> countText = new();
    private readonly Memo<(long Spent, long Max)> spentText = new();
    private readonly Dictionary<Guid, string> promptLines = new();
    private IReadOnlyList<WaitingRun>? promptLinesFor;
    private bool disposed;

    /// <summary>The run whose Start now was pressed while another is in hand: the HUD asks before stopping that one.</summary>
    private Guid? confirmStart;

    /// <summary>Until when "Copy the list" says it's copied.</summary>
    private DateTime copiedUntil;

    public HudWindow(LedgerSession session, Configuration config)
        : base("Market Ledger HUD###MarketLedgerHud", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar
                                                     | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize
                                                     | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoDocking)
    {
        this.session = session;
        this.config = config;
        fonts = Ui.AcquireFonts();
        RespectCloseHotkey = false;
        ShowCloseButton = false;
        DisableWindowSounds = true;
        AllowPinning = false;
        AllowClickthrough = false;
        PositionCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>Opens the main window on Run (set by the plugin); without it, the open button goes to the site.</summary>
    public Action? OpenMain { get; set; }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Ui.ReleaseFonts();
    }

    /// <summary>The first run sent from the site that hasn't been answered here (null when none).</summary>
    private WaitingRun? Prompt()
    {
        var inbox = session.Inbox;
        for (var i = 0; i < inbox.Count; i++)
        {
            var w = inbox[i];
            if (!w.Later && !notNow.Contains(w.Id)) return w;
        }
        return null;
    }

    private static bool InHand(RunState? run) => run != null && Ui.Going(run.Status);

    public override void PreOpenCheck()
    {
        var run = session.Current;
        ObserveEnd(run);
        IsOpen = config.ShowHud && (InHand(run) || Prompt() != null || Ended() != null);
    }

    // ---------------------------------------------------------------------------------------------
    // A run that has just ended: the HUD says so (and why, when it was stopped or failed, by you, the
    // site, or the run deleted there) for a while, rather than vanishing with it.

    private sealed record EndedRun(string Id, string Name, RunStatus Status, string? Message);

    private static readonly TimeSpan EndedShown = TimeSpan.FromSeconds(30);
    private EndedRun? ended;
    private DateTime endedUntil;
    private string? seenId;
    private bool seenGoing;

    /// <summary>Notices a run in hand ending (watched each frame, the HUD open or not).</summary>
    private void ObserveEnd(RunState? run)
    {
        var id = run?.File.Id;
        var going = InHand(run);
        if (run != null && id == seenId && seenGoing && !going)
        {
            ended = new EndedRun(run.File.Id, run.File.Name, run.Status, run.Message);
            endedUntil = DateTime.UtcNow + EndedShown;
        }
        if (going) ended = null;
        seenId = id;
        seenGoing = going;
    }

    private EndedRun? Ended() => ended != null && DateTime.UtcNow < endedUntil ? ended : null;

    private int colors;
    private int vars;

    public override void PreDraw()
    {
        // The first time ever: top right, out of the way (then where it was left; ImGui keeps it).
        var vp = ImGuiHelpers.MainViewport;
        Position = new Vector2(vp.Pos.X + vp.Size.X - S(Width) - S(24), vp.Pos.Y + S(24));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Palette.WithAlpha(Palette.Card, 0.97f));
        ImGui.PushStyleColor(ImGuiCol.Border, Palette.GoldEdge);
        ImGui.PushStyleColor(ImGuiCol.Text, Palette.Ink);
        colors = 3;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, S(Palette.RadiusLg));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        vars = 3;
    }

    public override void PostDraw()
    {
        if (vars > 0) ImGui.PopStyleVar(vars);
        if (colors > 0) ImGui.PopStyleColor(colors);
        vars = colors = 0;
    }

    public override void Draw()
    {
        Kit.ResetFrame();
        Theme.PushInside();
        var origin = ImGui.GetCursorScreenPos();
        Kit.PushBox(origin.X + S(Pad), S(Width - Pad * 2));
        try
        {
            using (fonts.Push(Face.Sans, 14))
            {
                Kit.Reserve(origin, S(Width), S(Pad - 2));
                var run = session.Current;
                var prompt = Prompt();
                if (InHand(run)) DrawRun(run!);
                else if (Ended() is { } justEnded)
                {
                    DrawEnded(justEnded);
                    if (prompt != null) Kit.Gap(12);
                }
                if (prompt != null)
                {
                    if (InHand(run)) Kit.Gap(12);
                    DrawPrompt(prompt, InHand(run), run);
                }
                Kit.Gap(Pad);
            }
        }
        finally
        {
            Kit.PopBox();
            Theme.Pop();
        }
        // The full width, padding on the right included, for the auto size.
        var end = ImGui.GetCursorScreenPos();
        Kit.Reserve(new Vector2(origin.X, end.Y), S(Width), 0);

        if (later.Count > 0)
        {
            var actions = later.ToArray();
            later.Clear();
            foreach (var a in actions)
            {
                try
                {
                    a();
                }
                catch (Exception ex)
                {
                    Services.Log.Error(ex, "Market Ledger: an action from the HUD failed");
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The run in hand.

    private void DrawRun(RunState run)
    {
        var at = Kit.At;
        var w = Kit.Width;
        var dl = Kit.Dl;
        var step = Ui.StepNow(run);
        var rowH = S(28);

        // The coins, the step's name, "step 2 / 7 · 0:11", Pause or Resume, open.
        var tile = S(22);
        dl.AddRectFilled(new Vector2(at.X, at.Y + (rowH - tile) / 2), new Vector2(at.X + tile, at.Y + (rowH + tile) / 2), Palette.U32(Palette.GoldTile), S(6));
        Icons.Draw(dl, Icon.Coins, new Vector2(at.X + S(4), at.Y + (rowH - S(14)) / 2), S(14), Palette.Gold);
        var bx = at.X + w - rowH;
        if (Kit.IconButton("##hud-open", new Vector2(bx, at.Y), Icon.ExternalLink, "Open the run window", 28, Btn.Quiet))
        {
            if (OpenMain != null) later.Add(OpenMain);
            else Dalamud.Utility.Util.OpenLink(Ui.RunUrl(run.File.Id));
        }
        bx -= rowH + S(4);
        var paused = run.Status is RunStatus.Paused or RunStatus.Accepted;
        if (Kit.IconButton("##hud-pause", new Vector2(bx, at.Y), paused ? Icon.Play : Icon.Pause, paused ? "Resume" : "Pause", 28,
                paused ? Btn.Picked : Btn.Glass))
        {
            if (paused) later.Add(session.Resume);
            else later.Add(session.Pause);
        }
        var head = HeadText(run);
        var hw = Txt.Measure(head, Face.Mono, 12).X;
        var hx = bx - S(10) - hw;
        Txt.Line(dl, new Vector2(hx, at.Y), head, Face.Mono, 12, Palette.Muted, 28);
        var nx = at.X + tile + S(8);
        var name = step?.Label ?? Ui.StatusWord(run.Status);
        Txt.Line(dl, new Vector2(nx, at.Y), Txt.Fit(name, Face.SansSemibold, 14, hx - S(10) - nx), Face.SansSemibold, 14, Palette.Ink, 28);
        Kit.Reserve(at, w, rowH);
        Kit.Gap(8);

        DrawStrip(run);

        // The step's count and what it's doing.
        var doing = run.Doing;
        var count = step is { } s0 && s0.Of > 0 ? countText.Get(((long)s0.Done << 32) | (uint)s0.Of, static k => $"{Ui.Count((int)(k >> 32))} / {Ui.Count((int)(k & 0xFFFFFFFF))}") : null;
        if (!string.IsNullOrEmpty(doing) || count != null)
        {
            Kit.Gap(8);
            var line = Kit.At;
            var cw = count != null ? Txt.Measure(count, Face.Mono, 12).X : 0;
            if (count != null) Txt.Line(dl, new Vector2(line.X + w - cw, line.Y), count, Face.Mono, 12, Palette.GoldSoft, 18);
            var dh = !string.IsNullOrEmpty(doing)
                ? Lines.Draw(dl, line, w - (cw > 0 ? cw + S(12) : 0), doing, Face.Sans, 12, Palette.Soft, 18)
                : S(18);
            Kit.Reserve(line, w, MathF.Max(dh, S(18)));
        }

        if (step?.Kind == "market") DrawBuying(run);

        if ((run.HeldForYou || run.Status is RunStatus.Waiting or RunStatus.Paused) && !string.IsNullOrEmpty(run.Message))
        {
            Kit.Gap(8);
            Lines.Para(run.Message, Palette.GoldText, 12, 18);
        }
        if (!string.IsNullOrEmpty(run.CopyText))
        {
            Kit.Gap(8);
            var c = Kit.At;
            var copy = run.CopyText;
            var copied = DateTime.UtcNow < copiedUntil;
            if (Kit.Button("##hud-copy", c, copied ? "Copied" : "Copy the list", Btn.Glass, BtnSize.Tiny, copied ? Icon.Check : Icon.Clipboard, w, false,
                    "Copies what this step needs you to paste (GatherBuddy Reborn: its Auto-Gather tab, Import from TeamCraft)"))
            {
                ImGui.SetClipboardText(copy);
                copiedUntil = DateTime.UtcNow + TimeSpan.FromSeconds(1.5);
            }
            Kit.Reserve(c, w, Kit.ButtonHeight(BtnSize.Tiny));
        }
        if (run.HeldForYou)
        {
            Kit.Gap(10);
            var b = Kit.At;
            if (Kit.Button("##hud-continue", b, "Continue", Btn.Primary, BtnSize.Sm, Icon.Play, w, false,
                    "Lets the step it's holding go ahead"))
                later.Add(session.Resume);
            Kit.Reserve(b, w, Kit.ButtonHeight(BtnSize.Sm));
        }
    }

    /// <summary>"Stopped · Gemdraughts for the weekend", why under it, and the way to the window; × puts it away.</summary>
    private void DrawEnded(EndedRun end)
    {
        var at = Kit.At;
        var w = Kit.Width;
        var dl = Kit.Dl;
        var rowH = S(28);
        var done = end.Status == RunStatus.Done;
        var tint = done ? Palette.Gain : Palette.Danger;
        var tile = S(22);
        dl.AddRectFilled(new Vector2(at.X, at.Y + (rowH - tile) / 2), new Vector2(at.X + tile, at.Y + (rowH + tile) / 2),
            Palette.U32(done ? Palette.SuccessWash : Palette.DangerWash), S(6));
        Icons.Draw(dl, done ? Icon.Check : Icon.AlertTriangle, new Vector2(at.X + S(4), at.Y + (rowH - S(14)) / 2), S(14), tint);
        var bx = at.X + w - rowH;
        if (Kit.IconButton("##hud-ended-close", new Vector2(bx, at.Y), Icon.X, "Put this away", 28, Btn.Quiet)) ended = null;
        bx -= rowH + S(4);
        if (Kit.IconButton("##hud-ended-open", new Vector2(bx, at.Y), Icon.ExternalLink, "Open the run window", 28, Btn.Quiet))
        {
            if (OpenMain != null) later.Add(OpenMain);
            else Dalamud.Utility.Util.OpenLink(Ui.RunUrl(end.Id));
        }
        var x = at.X + tile + S(8);
        x += Txt.Line(dl, new Vector2(x, at.Y), Ui.StatusWord(end.Status), Face.SansSemibold, 14, tint, 28);
        x += Txt.Line(dl, new Vector2(x, at.Y), " · ", Face.Sans, 13, Palette.Faint, 28);
        Txt.Line(dl, new Vector2(x, at.Y), Txt.Fit(end.Name, Face.SansSemibold, 14, MathF.Max(S(20), bx - S(10) - x)), Face.SansSemibold, 14, Palette.Ink, 28);
        Kit.Reserve(at, w, rowH);
        if (!string.IsNullOrEmpty(end.Message))
        {
            Kit.Gap(6);
            Lines.Para(end.Message, done ? Palette.Soft : Palette.Danger, 12, 18);
        }
    }

    /// <summary>"step 2 / 7 · 0:11", made again each second.</summary>
    private string HeadText(RunState run)
    {
        var secs = (long)Ui.Elapsed(run).TotalSeconds;
        var key = (secs << 16) | ((long)(run.StepIndex & 0xFF) << 8) | (long)(run.Steps.Count & 0xFF);
        return headText.Get(key, run, static (k, r) =>
        {
            var clock = r.StartedAt != null ? " · " + Ui.Clock(TimeSpan.FromSeconds(k >> 16)) : string.Empty;
            return $"step {r.StepIndex + 1} / {r.Steps.Count}{clock}";
        });
    }

    /// <summary>The strip: a segment for each step, gold behind, a darker gold for the one under way (it breathes), grey ahead, red where one failed.</summary>
    private static void DrawStrip(RunState run)
    {
        var steps = run.Steps;
        var at = Kit.At;
        var w = Kit.Width;
        var h = S(4);
        if (steps.Count == 0)
        {
            Kit.Reserve(at, w, h);
            return;
        }
        var gap = S(4);
        var seg = MathF.Max(S(2), (w - gap * (steps.Count - 1)) / steps.Count);
        var dl = Kit.Dl;
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            var x = at.X + (seg + gap) * i;
            Vector4 c;
            if (s.State == StepState.Failed) c = Palette.Loss;
            else if (s.State is StepState.Done) c = Palette.Gold;
            else if (s.State is StepState.Skipped) c = Palette.Faint;
            else if (i == run.StepIndex) c = run.Status == RunStatus.Running ? Mix(Palette.GoldDim, Palette.Gold, Pulse() * 0.5f) : Palette.GoldDim;
            else c = Palette.Track;
            dl.AddRectFilled(new Vector2(x, at.Y), new Vector2(x + seg, at.Y + h), Palette.U32(c), h / 2);
        }
        Kit.Reserve(at, w, h);
    }

    private static Vector4 Mix(Vector4 a, Vector4 b, float t) => a + (b - a) * t;

    /// <summary>On the market: what it buys, and the gil spent of what was agreed on the site, with its bar.</summary>
    private void DrawBuying(RunState run)
    {
        Kit.Gap(10);
        var dl = Kit.Dl;
        var w = Kit.Width;
        var max = (long)run.File.MaxGil;
        var at = Kit.At;
        Kit.Label(new Vector2(at.X, at.Y + S(2)), "BUYING · SPENT OF AGREED", Palette.Muted);
        var spent = spentText.Get((run.Spent, max), static k => k.Max > 0 ? $"{Ui.Gil(k.Spent)} / {Ui.Gil(k.Max)}" : Ui.Gil(k.Spent));
        var sw = Txt.Measure(spent, Face.Mono, 12).X;
        var cx = at.X + w - S(10);
        Kit.Coin(new Vector2(cx, at.Y), S(18));
        Txt.Line(dl, new Vector2(cx - sw, at.Y), spent, Face.Mono, 12, max > 0 && run.Spent > max ? Palette.Loss : Palette.Ink, 18);
        Kit.Reserve(at, w, S(18));
        if (max > 0)
        {
            Kit.Gap(6);
            var p = Kit.At;
            Kit.Progress(p, w, (float)run.Spent / max, 4, run.Spent > max ? Palette.Loss : Palette.Gold);
            Kit.Reserve(p, w, S(4));
        }
        Kit.Gap(6);
        Lines.Para(config.TrustClicks
            ? "It buys by itself at or under each line's price, never past the agreed gil."
            : "It buys at or under each line's price; a click not yet seen working waits for Continue.", Palette.Faint, 11, 16);
    }

    // ---------------------------------------------------------------------------------------------
    // A run from the site.

    private void DrawPrompt(WaitingRun run, bool inHand, RunState? current)
    {
        // The question was about another run, or nothing is in hand any more: it's asked afresh.
        if (confirmStart is { } asked && (asked != run.Id || !inHand)) confirmStart = null;
        if (inHand)
        {
            var line = Kit.At;
            Kit.Divider(line.X - S(Pad) + 1, line.X + Kit.Width + S(Pad) - 1, line.Y);
            Kit.Gap(12);
        }
        var at = Kit.At;
        var w = Kit.Width;
        var dl = Kit.Dl;
        var tile = S(32);
        dl.AddRectFilled(at, at + new Vector2(tile), Palette.U32(Palette.GoldTile), S(8));
        Icons.Draw(dl, Icon.Send, at + new Vector2((tile - S(16)) / 2), S(16), Palette.Gold);
        var x = at.X + tile + S(12);
        var tw = w - (x - at.X);
        Txt.Line(dl, new Vector2(x, at.Y), "A new run from kokos-tools.com", Face.SansSemibold, 13, Palette.Ink, 18);
        Txt.Line(dl, new Vector2(x, at.Y + S(18)), Txt.Fit(PromptLine(run), Face.Sans, 12, tw), Face.Sans, 12, Palette.Muted, 18);
        Kit.Reserve(at, w, MathF.Max(tile, S(36)));
        Kit.Gap(10);

        var row = Kit.At;
        var h = Kit.ButtonHeight(BtnSize.Tiny);
        var id = run.Id;
        if (confirmStart == id && current != null)
        {
            DrawStartOver(run, current, row, w, h);
            return;
        }
        const string start = "Start now";
        var second = inHand ? "After this run" : "Not now";
        var sw = Kit.ButtonWidth(start, BtnSize.Tiny, Icon.Play, Btn.Primary);
        var lw = Kit.ButtonWidth(second, BtnSize.Tiny);
        if (Kit.Button("##hud-start", new Vector2(row.X + w - sw, row.Y), start, Btn.Primary, BtnSize.Tiny, Icon.Play, 0, false,
                inHand ? "Asks first: the run in hand would be stopped where it is (the site is told)" : null))
        {
            // Over a run in hand it asks first, as the main window does; otherwise it starts.
            if (inHand) confirmStart = id;
            else later.Add(() => session.Start(id));
        }
        if (Kit.Button("##hud-later", new Vector2(row.X + w - sw - S(8) - lw, row.Y), second, Btn.Glass, BtnSize.Tiny, null, 0, false,
                inHand ? "Starts by itself once the run in hand is done" : "It waits on Home in the main window"))
        {
            if (inHand) later.Add(() => session.Later(id));
            else notNow.Add(id);
        }
        Kit.Reserve(row, w, h);
    }

    /// <summary>Start now pressed over a run in hand: "Stop "…" and start this one?" with Keep going and Stop and start.</summary>
    private void DrawStartOver(WaitingRun run, RunState current, Vector2 row, float w, float h)
    {
        var line = stopLine.Get(current.File.Id, current.File.Name, static (_, name) => $"\"{name}\" is stopped where it is (the site is told), and this one starts.");
        Lines.Para(line, Palette.GoldText, 12, 18);
        Kit.Gap(8);
        row = Kit.At;
        const string yes = "Stop it and start";
        const string no = "Keep going";
        var yw = Kit.ButtonWidth(yes, BtnSize.Tiny, Icon.Play, Btn.Danger);
        var nw = Kit.ButtonWidth(no, BtnSize.Tiny);
        var id = run.Id;
        if (Kit.Button("##hud-start-yes", new Vector2(row.X + w - yw, row.Y), yes, Btn.Danger, BtnSize.Tiny, Icon.Play))
        {
            confirmStart = null;
            later.Add(() => session.Start(id));
        }
        if (Kit.Button("##hud-start-no", new Vector2(row.X + w - yw - S(8) - nw, row.Y), no, Btn.Glass, BtnSize.Tiny))
            confirmStart = null;
        Kit.Reserve(row, w, h);
    }

    private readonly Memo<string> stopLine = new();

    /// <summary>"Gemdraughts for the weekend · about 40 min · up to 120,000 gil", once per run.</summary>
    private string PromptLine(WaitingRun run)
    {
        // A new list from the site: the lines made for the old one go.
        if (!ReferenceEquals(session.Inbox, promptLinesFor))
        {
            promptLinesFor = session.Inbox;
            promptLines.Clear();
        }
        if (promptLines.TryGetValue(run.Id, out var line)) return line;
        if (promptLines.Count > 32) promptLines.Clear();
        var max = (long)run.File.MaxGil;
        var minutes = (double)run.File.Estimate.Minutes;
        line = run.Name
               + (minutes > 0 ? $" · {Ui.About(minutes)}" : string.Empty)
               + (max > 0 ? $" · up to {Ui.Gil(max)} gil" : string.Empty);
        return promptLines[run.Id] = line;
    }
}
