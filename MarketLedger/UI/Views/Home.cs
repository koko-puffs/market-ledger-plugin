using System;
using System.Collections.Generic;
using System.Numerics;
using MarketLedger.Core;
using MarketLedger.Session;
using static MarketLedger.UI.Theme;

namespace MarketLedger.UI;

/// <summary>
/// Home: the link (a box to link when there isn't one), who you are in the game, the run in hand
/// (its step, how far, the time and the gil, Pause / Resume / Skip / Stop, and Continue when a
/// click is held for you), and the runs sent from the site, each with Start now and After this run.
/// </summary>
public sealed partial class MainWindow
{
    private readonly Memo<long> elapsedText = new();
    private readonly Memo<long> leftText = new();
    private readonly Memo<long> spentText = new();
    private readonly Memo<long> stepsText = new();
    private readonly Memo<long> gilText = new();
    private readonly Memo<int> slotsText = new();
    private readonly Dictionary<Guid, string> inboxLines = new();
    private readonly List<Kit.Fact> runFacts = new(4);
    private readonly Memo<int> waitingWords = new();

    private void DrawHomeTab()
    {
        Frame.BeginBody("##home", null, 20, 24);
        if (session.Link is not LinkState.Linked) DrawLinkBox();
        if (session.Character is { } who) DrawYou(who);

        var run = session.Current;
        Kit.Section("The run in hand", run != null ? Ui.StatusWord(run.Status) : null);
        Kit.Gap(10);
        if (run != null) DrawRunCard(run);
        else DrawNoRun();

        Kit.Gap(26);
        DrawInbox(run);
        Frame.EndBody();
    }

    // ---------------------------------------------------------------------------------------------
    // The link.

    private void DrawLinkBox()
    {
        var link = session.Link;
        Kit.BeginPanel("##home-link", 16, 18, 16, Palette.GoldWash, Palette.GoldEdge);
        var at = Kit.At;
        var w = Kit.Width;
        Kit.IconWell(at, S(36), Icon.Link);
        var x = at.X + S(36 + 14);
        var tw = w - (x - at.X);
        var (title, line) = link switch
        {
            LinkState.Linking => ("Linking to kokos-tools.com…", "A moment: the plugin is checking its token with the site."),
            LinkState.Refused r => ("The site didn't take the token", r.Why),
            _ => ("Link the plugin to your account", "Runs are planned on kokos-tools.com and sent here. Make a token on the site and paste it in Settings, once."),
        };
        Txt.Line(Kit.Dl, new Vector2(x, at.Y), Txt.Fit(title, Face.SansMedium, 14, tw), Face.SansMedium, 14, Palette.Ink);
        var nameH = Txt.Natural(Face.SansMedium, 14);
        var lineH = Lines.Draw(Kit.Dl, new Vector2(x, at.Y + nameH + S(2)), tw, line, Face.Sans, 13,
            link is LinkState.Refused ? Palette.Loss : Palette.Muted, 19.5f);
        Kit.Reserve(at, w, MathF.Max(S(36), nameH + S(2) + lineH));
        if (link is not LinkState.Linking)
        {
            Kit.Gap(12);
            var row = new Wrap(8);
            const string set = "Paste a token";
            const string get = "Get a token";
            var setW = Kit.ButtonWidth(set, BtnSize.Tiny, Icon.Link, Btn.Primary);
            if (Kit.Button("##home-link-set", row.Next(setW, Kit.ButtonHeight(BtnSize.Tiny)), set, Btn.Primary, BtnSize.Tiny, Icon.Link))
                tab = MainTab.Settings;
            var getW = Kit.ButtonWidth(get, BtnSize.Tiny, Icon.ExternalLink);
            if (Kit.Button("##home-link-get", row.Next(getW, Kit.ButtonHeight(BtnSize.Tiny)), get, Btn.Glass, BtnSize.Tiny, Icon.ExternalLink,
                    0, false, "Opens Market Ledger's settings on the site, where tokens are made"))
                Dalamud.Utility.Util.OpenLink(Ui.TokensUrl);
            row.End();
        }
        Kit.EndPanel();
        Kit.Gap(20);
    }

    // ---------------------------------------------------------------------------------------------
    // You.

    private void DrawYou(CharacterSnapshot who)
    {
        var at = Kit.At;
        var w = Kit.Width;
        var dl = Kit.Dl;
        var x = at.X;
        x += Txt.Line(dl, new Vector2(x, at.Y), who.Name, Face.SansMedium, 13, Palette.Ink, 22);
        x += Txt.Line(dl, new Vector2(x, at.Y), " · ", Face.Sans, 13, Palette.Faint, 22);
        x += Txt.Line(dl, new Vector2(x, at.Y), who.World, Face.Sans, 13, Palette.Muted, 22);
        // On the right: the gil and the free slots, numbers in mono.
        var right = at.X + w;
        if (who.FreeSlots is { } slots)
        {
            var s = slotsText.Get(slots, static v => Ui.Plural(v, "free slot", "free slots"));
            var sw = Txt.Measure(s, Face.Mono, 12).X;
            right -= sw;
            Txt.Line(dl, new Vector2(right, at.Y), s, Face.Mono, 12, slots < config.KeepFreeSlots ? Palette.Loss : Palette.Soft, 22);
            right -= S(16);
        }
        if (who.Gil is { } gil)
        {
            var g = gilText.Get(gil, static v => Ui.Gil(v));
            right -= S(12);
            Kit.Coin(new Vector2(right, at.Y), S(22));
            right -= Txt.Measure(g, Face.Mono, 12).X;
            Txt.Line(dl, new Vector2(right, at.Y), g, Face.Mono, 12, Palette.Soft, 22);
        }
        Kit.Reserve(at, w, S(22));
        Kit.Gap(18);
    }

    // ---------------------------------------------------------------------------------------------
    // The run in hand.

    private void DrawNoRun()
    {
        Kit.BeginPanel("##home-norun", 16, 18, 16);
        var at = Kit.At;
        var w = Kit.Width;
        Kit.IconWell(at, S(36), Icon.Coins, quiet: true);
        var x = at.X + S(36 + 14);
        var tw = w - (x - at.X);
        Txt.Line(Kit.Dl, new Vector2(x, at.Y), "Nothing running", Face.SansMedium, 14, Palette.Ink);
        var nameH = Txt.Natural(Face.SansMedium, 14);
        var h = Lines.Draw(Kit.Dl, new Vector2(x, at.Y + nameH + S(2)), tw,
            "Plan what to make on kokos-tools.com and send it to the game: it waits below until you start it.", Face.Sans, 13, Palette.Muted, 19.5f);
        Kit.Reserve(at, w, MathF.Max(S(36), nameH + S(2) + h));
        Kit.Gap(12);
        if (Kit.Button("##home-site", Kit.At, "Plan a run on kokos-tools.com", Btn.Glass, BtnSize.Tiny, Icon.ExternalLink))
            Dalamud.Utility.Util.OpenLink(Ui.SiteUrl);
        Kit.Reserve(Kit.At, w, Kit.ButtonHeight(BtnSize.Tiny));
        Kit.EndPanel();
    }

    private void DrawRunCard(RunState run)
    {
        var live = Ui.Going(run.Status);
        if (live) Kit.BeginPanel("##home-run", 18, 18, 18, Palette.GoldWash, Palette.GoldEdge);
        else Kit.BeginPanel("##home-run", 18, 18, 18);
        var at = Kit.At;
        var w = Kit.Width;
        var dl = Kit.Dl;
        var step = Ui.StepNow(run);

        // The eyebrow: the step and the state, the pill on the right.
        var status = Ui.StatusWord(run.Status);
        var pillW = Kit.StatusPillWidth(status);
        Kit.StatusPill(new Vector2(at.X + w - pillW, at.Y), status, Ui.StatusTone(run.Status), run.Status == RunStatus.Running);
        var eyebrow = step != null ? StepEyebrow(run) : "Not started";
        Kit.Eyebrow(at, eyebrow, Palette.GoldText, w - pillW - S(12));
        var titleY = at.Y + Txt.Natural(Face.SansMedium, 12) + S(4);
        var titleH = Title(new Vector2(at.X, titleY), run.File.Name, w - pillW - S(12), 18, 2);
        var y = titleY + titleH + S(2);
        var doing = run.Doing ?? step?.Label;
        if (!string.IsNullOrEmpty(doing))
            y += Lines.Draw(dl, new Vector2(at.X, y), w, doing, Face.Sans, 13, Palette.Soft, 19.5f);
        Kit.Reserve(at, w, MathF.Max(S(28), y - at.Y));
        Kit.Gap(12);

        var bar = Kit.At;
        Kit.Progress(bar, w, Ui.Fraction(run));
        Kit.Reserve(bar, w, S(8));
        Kit.Gap(14);

        Kit.Facts("##home-facts", RunFacts(run));

        if (!string.IsNullOrEmpty(run.Message))
        {
            Kit.Gap(12);
            Lines.Para(run.Message, MessageColor(run.Status));
        }
        Kit.Gap(16);
        RunButtons(0, run, true);
        Kit.EndPanel();
    }

    private readonly Memo<(long Step, string Run)> eyebrowText = new();

    /// <summary>"Step 3 of 7 · Gather", made again only when the step moves.</summary>
    private string StepEyebrow(RunState run)
    {
        var key = (((long)run.StepIndex << 20) | (long)run.Steps.Count, run.File.Id);
        return eyebrowText.Get(key, run, static (_, r) =>
        {
            var s = Ui.StepNow(r);
            var kind = s?.Kind switch
            {
                "withdraw" => "Take from retainers",
                "vendor" => "Buy from vendors",
                "market" => "Buy on the market",
                "gather" => "Gather",
                "craft" => "Craft",
                "entrust" => "Entrust to retainers",
                _ => null,
            };
            var head = $"Step {r.StepIndex + 1} of {r.Steps.Count}";
            return kind != null ? $"{head} · {kind}" : head;
        });
    }

    private static Vector4 MessageColor(RunStatus s) => s switch
    {
        RunStatus.Failed or RunStatus.Stopped => Palette.Loss,
        RunStatus.Waiting or RunStatus.Paused => Palette.GoldText,
        _ => Palette.Muted,
    };

    /// <summary>The run's facts: steps, elapsed, left, spent of the most it may spend.</summary>
    private List<Kit.Fact> RunFacts(RunState run)
    {
        runFacts.Clear();
        var of = run.Steps.Count;
        var done = Ui.StepsDone(run);
        runFacts.Add(new Kit.Fact("Steps", stepsText.Get(((long)done << 20) | (long)of, static k => $"{k >> 20} / {k & 0xFFFFF}")));
        runFacts.Add(new Kit.Fact("Elapsed", ElapsedText(run)));
        runFacts.Add(new Kit.Fact("Left, about", LeftText(run)));
        var max = (long)run.File.MaxGil;
        runFacts.Add(new Kit.Fact("Spent", spentText.Get(run.Spent, static v => Ui.Gil(v)), MaxGilNote(max),
            max > 0 && run.Spent > max ? Palette.Loss : null));
        return runFacts;
    }

    private readonly Memo<long> maxGilNote = new();

    private string MaxGilNote(long max) => maxGilNote.Get(max, static v => v > 0 ? $"of {Ui.Gil(v)}" : "no gil agreed");

    private string ElapsedText(RunState run) =>
        run.StartedAt == null ? "—" : elapsedText.Get((long)Ui.Elapsed(run).TotalSeconds, static s => Ui.Clock(TimeSpan.FromSeconds(s)));

    private string LeftText(RunState run) =>
        run.Left is { } left ? leftText.Get((long)left.TotalMinutes, static m => Ui.Minutes(m)) : "—";

    /// <summary>
    /// The run's buttons, wrapping when narrow: Continue (when a click is held for you), Pause or
    /// Resume, Skip step, Stop; and Open on kokos-tools.com when asked. Ended runs have only the link.
    /// </summary>
    private void RunButtons(int where, RunState run, bool withOpen)
    {
        var row = new Wrap(8);
        var h = Kit.ButtonHeight(BtnSize.Tiny);
        if (Ui.Going(run.Status))
        {
            if (run.HeldForYou)
            {
                const string go = "Continue";
                if (Kit.Button(Ids.Of("##run-continue", where), row.Next(Kit.ButtonWidth(go, BtnSize.Tiny, Icon.Play, Btn.Primary), h), go, Btn.Primary, BtnSize.Tiny, Icon.Play,
                        0, false, "Lets the step it's holding go ahead (a market click it hasn't been trusted with yet, or a question in the game)"))
                    Later(session.Resume);
            }
            if (run.Status == RunStatus.Paused || run.Status == RunStatus.Accepted)
            {
                var label = run.Status == RunStatus.Accepted ? "Start" : "Resume";
                var style = run.Status == RunStatus.Accepted ? Btn.Primary : Btn.Glass;
                if (Kit.Button(Ids.Of("##run-resume", where), row.Next(Kit.ButtonWidth(label, BtnSize.Tiny, Icon.Play, style), h), label, style, BtnSize.Tiny, Icon.Play))
                    Later(session.Resume);
            }
            else
            {
                const string pause = "Pause";
                if (Kit.Button(Ids.Of("##run-pause", where), row.Next(Kit.ButtonWidth(pause, BtnSize.Tiny, Icon.Pause), h), pause, Btn.Glass, BtnSize.Tiny, Icon.Pause,
                        0, false, "Stops where it is, safely, and waits for Resume"))
                    Later(session.Pause);
            }
            const string skip = "Skip step";
            if (Kit.Button(Ids.Of("##run-skip", where), row.Next(Kit.ButtonWidth(skip, BtnSize.Tiny, Icon.SkipForward), h), skip, Btn.Glass, BtnSize.Tiny, Icon.SkipForward,
                    0, false, "Leaves this step as it is and goes on to the next"))
                Ask("Skip this step?", "Market Ledger leaves the step as it is and goes on to the next. Steps after it may come up short of what they need.",
                    "Skip it", "Keep going", session.SkipStep);
            const string stop = "Stop";
            if (Kit.Button(Ids.Of("##run-stop", where), row.Next(Kit.ButtonWidth(stop, BtnSize.Tiny, Icon.Square, Btn.Danger), h), stop, Btn.Danger, BtnSize.Tiny, Icon.Square))
                Ask("Stop this run?", "It stops where it is and hands the helpers back. What's done stays done, and the site keeps the run's progress.",
                    "Stop the run", "Keep going", session.Stop, danger: true);
        }
        if (withOpen)
        {
            const string open = "Open on kokos-tools.com";
            if (Kit.Button(Ids.Of("##run-open", where), row.Next(Kit.ButtonWidth(open, BtnSize.Tiny, Icon.ExternalLink, Btn.Quiet), h), open, Btn.Quiet, BtnSize.Tiny, Icon.ExternalLink))
                Dalamud.Utility.Util.OpenLink(Ui.RunUrl(run.File.Id));
        }
        row.End();
    }

    // ---------------------------------------------------------------------------------------------
    // Runs sent from the site.

    private void DrawInbox(RunState? current)
    {
        var inbox = session.Inbox;
        Kit.Section("Sent from the site", inbox.Count == 0 ? null : waitingWords.Get(inbox.Count, static n => $"{n} waiting"));
        Kit.Gap(10);
        if (inbox.Count == 0)
        {
            Lines.Para("Runs you send from kokos-tools.com wait here until you start them. They arrive by themselves while the plugin is linked.");
            return;
        }
        if (inboxLines.Count > 64) inboxLines.Clear();
        var going = current != null && Ui.Going(current.Status);
        Kit.BeginPanel("##home-inbox", 6, 6, 6);
        var dl = Kit.Dl;
        for (var i = 0; i < inbox.Count; i++)
        {
            var run = inbox[i];
            var at = Kit.At;
            var w = Kit.Width;
            var nameH = Txt.Natural(Face.SansMedium, 14);
            var h = S(12) * 2 + MathF.Max(S(36), nameH + S(2) + S(19.5f));
            if (i > 0) dl.AddLine(new Vector2(at.X + S(10), at.Y - 0.5f), new Vector2(at.X + w - S(10), at.Y - 0.5f), Palette.U32(Palette.Line), 1f);

            // The buttons on the right, laid first so the words know their room.
            var bh = Kit.ButtonHeight(BtnSize.Tiny);
            var by = at.Y + (h - bh) / 2;
            var x = at.X + w - S(10);
            x -= bh;
            if (Kit.IconButton(Ids.Of("##inbox-open", i), new Vector2(x, by), Icon.ExternalLink, "Open it on kokos-tools.com", 30, Btn.Quiet))
                Dalamud.Utility.Util.OpenLink(Ui.RunUrl(run.Id.ToString()));
            const string start = "Start now";
            var sw = Kit.ButtonWidth(start, BtnSize.Tiny, Icon.Play, Btn.Primary);
            x -= S(8) + sw;
            if (Kit.Button(Ids.Of("##inbox-start", i), new Vector2(x, by), start, Btn.Primary, BtnSize.Tiny, Icon.Play))
            {
                var id = run.Id;
                if (going)
                    Ask("Start this one now?", "The run in hand stops where it is (the site keeps its progress) and this one starts.",
                        "Start it now", "Not yet", () => session.Start(id));
                else Later(() => session.Start(id));
            }
            if (going)
            {
                if (run.Later)
                {
                    const string queued = "After this run";
                    var cw = Kit.ChipW(queued, true);
                    x -= S(8) + cw;
                    Kit.Chip(new Vector2(x, at.Y + (h - S(24)) / 2), queued, ChipKind.Gold, true);
                }
                else
                {
                    const string after = "After this run";
                    var aw = Kit.ButtonWidth(after, BtnSize.Tiny);
                    x -= S(8) + aw;
                    if (Kit.Button(Ids.Of("##inbox-later", i), new Vector2(x, by), after, Btn.Glass, BtnSize.Tiny, null, 0, false,
                            "Starts by itself once the run in hand is done"))
                    {
                        var id = run.Id;
                        Later(() => session.Later(id));
                    }
                }
            }

            var tx = at.X + S(12);
            Kit.IconWell(new Vector2(tx, at.Y + (h - S(36)) / 2), S(36), Icon.Inbox, quiet: run.Later);
            tx += S(36 + 12);
            var textW = x - S(12) - tx;
            var top = at.Y + (h - (nameH + S(2) + S(19.5f))) / 2;
            Txt.Line(dl, new Vector2(tx, top), Txt.Fit(run.Name, Face.SansMedium, 14, textW), Face.SansMedium, 14, Palette.Ink);
            Txt.Line(dl, new Vector2(tx, top + nameH + S(2)), Txt.Fit(InboxLine(run), Face.Sans, 12, textW), Face.Sans, 12, Palette.Muted, 19.5f);
            Kit.Reserve(at, w, h);
        }
        Kit.EndPanel();
    }

    /// <summary>"Sent 07:51 · 7 steps · up to 120,000 gil · about 40 min", once per run.</summary>
    private string InboxLine(WaitingRun run)
    {
        if (inboxLines.TryGetValue(run.Id, out var line)) return line;
        var max = (long)run.File.MaxGil;
        var minutes = (double)run.File.Estimate.Minutes;
        line = $"Sent {Ui.When(run.SentAt)} · {Ui.Plural(System.Linq.Enumerable.Count(run.File.Steps), "step", "steps")}"
               + (max > 0 ? $" · up to {Ui.Gil(max)} gil" : string.Empty)
               + (minutes > 0 ? $" · {Ui.About(minutes)}" : string.Empty);
        return inboxLines[run.Id] = line;
    }
}
