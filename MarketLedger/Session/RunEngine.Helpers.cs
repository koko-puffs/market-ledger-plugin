using System;
using System.Collections.Generic;
using System.Linq;
using MarketLedger.Core;
using MarketLedger.Game;

namespace MarketLedger.Session;

/// <summary>The steps another plugin does: gathering (GatherBuddy Reborn) and crafting (Artisan).</summary>
public sealed partial class RunEngine
{
    // ---------------------------------------------------------------------------------------------
    // Gathering: GatherBuddy Reborn's auto-gather, watched through your bags.

    private bool gatherStarted;

    /// <summary>Waiting for you to put the list into GatherBuddy Reborn by hand (its IPC has no lists).</summary>
    private bool gatherHeld;

    /// <summary>The list made in GatherBuddy Reborn for this run, and the lists of yours it turned off meanwhile.</summary>
    private string? gatherList;

    private List<string>? listsTurnedOff;
    private DateTime? gatherOffSince;
    private DateTime gatherBegan;
    private string? gatherStatusSaid;
    private DateTime gatherStatusAt;

    /// <summary>Since when GatherBuddy Reborn hasn't answered whether it's on, and since when it's waited with nothing to wait for.</summary>
    private DateTime? gatherSilentSince;

    private DateTime? gatherIdleSince;

    /// <summary>How long GatherBuddy Reborn may wait with nothing it can gather before the run pauses (no timed node's window to come).</summary>
    private static readonly TimeSpan GatherIdleWait = TimeSpan.FromSeconds(90);

    private void BeginGather(RunStep step)
    {
        gatherStarted = false;
        gatherHeld = false;
        gatherOffSince = null;
        gatherSilentSince = null;
        gatherIdleSince = null;
        gatherBegan = DateTime.UtcNow;
        if (!GatherBuddy.Installed)
        {
            StepProblem("GatherBuddy Reborn isn't installed (or is turned off): install it, then Resume; or Skip this step.");
            return;
        }
        var lines = step.Lines.Where(l => !StepMath.LineDone(step.Kind, l, Have(step.Kind, l))).ToList();
        // How many to own in all, as GatherBuddy Reborn's lists count: the line's need.
        var items = new Dictionary<uint, uint>();
        foreach (var l in lines) items[l.Item] = (uint)Math.Max(items.GetValueOrDefault(l.Item), l.Need);

        var lists = GatherBuddy.Lists;
        if (lists != null && File != null)
        {
            var name = GatherList.Name(File.Name);
            var added = GatherBuddy.SetList(name, items, $"Made by Market Ledger for the run \"{File.Name}\"; deleted when the run's done.");
            if (added > 0)
            {
                gatherList = name;
                if (added < items.Count)
                    Say(HelperNames.GatherBuddy, $"Took {added} of {items.Count} items for its list (it doesn't gather the rest).");
                // Only this run's list: your own enabled lists are turned off meanwhile, and back on after.
                if (listsTurnedOff == null)
                {
                    listsTurnedOff = new List<string>();
                    foreach (var (other, on) in lists)
                        if (on && other != name && GatherBuddy.SetListEnabled(other, false))
                            listsTurnedOff.Add(other);
                    if (listsTurnedOff.Count > 0) Say(HelperNames.GatherBuddy, $"Turned off your lists meanwhile: {string.Join(", ", listsTurnedOff)}.");
                }
                if (!GatherBuddy.SetListEnabled(name, true))
                {
                    StepProblem("GatherBuddy Reborn wouldn't turn the run's list on (its own checks: perception, or bait for fish). Look at its Auto-Gather tab, then Resume.");
                    return;
                }
                Say(HelperNames.GatherBuddy, $"Made the list \"{name}\": {string.Join(", ", lines.Select(l => $"{l.Need} × {InGame.ItemName(l.Item)}"))} in all.");
                return;
            }
            if (added == 0)
            {
                StepProblem($"GatherBuddy Reborn doesn't gather {string.Join(", ", lines.Select(l => InGame.ItemName(l.Item)))}. Gather them yourself, then Resume; or Skip.");
                return;
            }
            // (-1: it didn't take the call after all: the way by hand, below.)
        }

        // No lists over its IPC (upstream, so far): the list is handed to you to import, then auto-gather is turned on.
        CopyText = GatherList.Teamcraft(lines.Select(l => (InGame.ItemName(l.Item), l.Need)));
        gatherHeld = true;
        Status = RunStatus.Waiting;
        HeldForYou = true;
        Message = "GatherBuddy Reborn can't take a list from another plugin yet. Copy this list, and in GatherBuddy Reborn's Auto-Gather tab "
                  + "make a new list, press \"Import from TeamCraft\", and leave only that list on. Then Resume, and auto-gather is turned on for you.";
        Doing = $"Waiting for you · {HelperNames.GatherBuddy}";
        Say(HelperNames.Ledger, "GatherBuddy Reborn has no list IPC: the list is ready to copy and import by hand.");
        Touch(true);
    }

    private void TickGather(RunStep step)
    {
        if (gatherHeld) return;
        var now = DateTime.UtcNow;
        var missing = step.Lines.Where(l => !StepMath.LineDone(step.Kind, l, Have(step.Kind, l))).ToList();
        if (missing.Count == 0)
        {
            if (gatherStarted && GatherBuddy.SetEnabled(false)) Say(HelperNames.GatherBuddy, "Auto-gather off: all gathered.");
            gatherStarted = false;
            if (gatherList != null) GatherBuddy.SetListEnabled(gatherList, false);
            EndStep();
            return;
        }
        if (!gatherStarted)
        {
            // One helper at a time: Artisan finishes first.
            if (Artisan.Busy == true)
            {
                Doing = $"Waiting for Artisan to finish · {HelperNames.Artisan}";
                if (now - gatherBegan > TimeSpan.FromMinutes(3)) StepProblem("Artisan is still busy after 3 minutes; stop it, then Resume.");
                return;
            }
            if (!GatherBuddy.SetEnabled(true))
            {
                StepProblem("GatherBuddy Reborn didn't answer when asked to auto-gather. Turn it on yourself (/gbr auto) and Resume, or Skip.");
                return;
            }
            gatherStarted = true;
            gatherOffSince = null;
            Say(HelperNames.GatherBuddy, "Auto-gather on.");
            Touch(true);
            return;
        }

        // Bags filling up: paused before they're full (from what was free when the step started).
        var free = InGame.FreeSlots();
        if (free == 0 || (config.KeepFreeSlots > 0 && free < config.KeepFreeSlots && free < slotsAtStepStart))
        {
            StepProblem($"Your bags are nearly full ({free} free slots). Make room, then Resume.");
            return;
        }

        var enabled = GatherBuddy.Enabled;
        var status = GatherBuddy.Status;
        if (status != null && status != gatherStatusSaid && now - gatherStatusAt > TimeSpan.FromSeconds(5))
        {
            gatherStatusSaid = status;
            gatherStatusAt = now;
            Say(HelperNames.GatherBuddy, status);
        }
        if (enabled == null)
        {
            // Not answering at all (unloaded, or another version): nothing to watch it by.
            gatherSilentSince ??= now;
            if (now - gatherSilentSince.Value > TimeSpan.FromSeconds(30))
                StepProblem("GatherBuddy Reborn stopped answering (unloaded, or updated?). Check it in Helpers, then Resume; or Skip.");
            return;
        }
        gatherSilentSince = null;
        if (enabled == false)
        {
            gatherOffSince ??= now;
            if (now - gatherOffSince.Value > TimeSpan.FromSeconds(5))
                StepProblem($"GatherBuddy Reborn stopped auto-gathering{(string.IsNullOrEmpty(status) ? string.Empty : $" (\"{status}\")")} with "
                            + $"{string.Join(", ", missing.Select(l => $"{StepMath.Missing(step.Kind, l, Have(step.Kind, l))} × {InGame.ItemName(l.Item)}"))} still to gather. Resume to try again, or Skip. (If it counts your retainers' items, its \"Check Retainer Inventories\" setting, it may think it has them already.)");
            return;
        }
        gatherOffSince = null;

        var first = missing[0];
        if (GatherBuddy.Waiting == true)
        {
            // Waiting is right while a timed node's window is still to open. Otherwise it has nothing it can
            // gather ("No available items to gather"): not for long, or the run would wait for ever.
            var windowToCome = missing.Any(l => l.Timed is { } w && !Eorzea.NextWindow(w, now).Open);
            if (windowToCome) gatherIdleSince = null;
            else
            {
                gatherIdleSince ??= now;
                if (now - gatherIdleSince.Value > GatherIdleWait)
                {
                    StepProblem($"GatherBuddy Reborn has nothing it can gather{(string.IsNullOrEmpty(status) ? string.Empty : $" (\"{status}\")")} with "
                                + $"{string.Join(", ", missing.Select(l => $"{StepMath.Missing(step.Kind, l, Have(step.Kind, l))} × {InGame.ItemName(l.Item)}"))} still to come to your bags. "
                                + "It may count what your retainers hold (its \"Check Retainer Inventories\" setting: the run counts your bags only), or not gather them at your level. Gather them yourself and Resume, or Skip.");
                    return;
                }
            }
            var words = TimedWords(missing) ?? $"GatherBuddy Reborn is waiting{(string.IsNullOrEmpty(status) ? "." : $": {status}")}";
            if (Status != RunStatus.Waiting || Message != words)
            {
                Status = RunStatus.Waiting;
                HeldForYou = false;
                Message = words;
                Touch(true);
            }
        }
        else
        {
            gatherIdleSince = null;
            if (Status == RunStatus.Waiting)
            {
                Status = RunStatus.Running;
                Message = null;
                Touch(true);
            }
        }
        var doing = $"Gathering {InGame.ItemName(first.Item)} · {HelperNames.GatherBuddy}";
        if (Doing != doing)
        {
            Doing = doing;
            Touch();
        }
    }

    /// <summary>"Waiting for Claro Walnut Log's window at 16:00 ET (in 4 min)", when a missing line is a timed node's.</summary>
    private static string? TimedWords(IReadOnlyList<RunLine> missing)
    {
        var now = DateTime.UtcNow;
        (RunLine Line, DateTime Opens, bool Open)? soonest = null;
        foreach (var l in missing)
        {
            if (l.Timed is not { } w) continue;
            var (opens, _, open) = Eorzea.NextWindow(w, now);
            if (soonest == null || opens < soonest.Value.Opens) soonest = (l, opens, open);
        }
        if (soonest is not { } s) return null;
        if (s.Open) return $"{InGame.ItemName(s.Line.Item)}'s window is open: GatherBuddy Reborn is on its way.";
        var et = Eorzea.Now(s.Opens);
        var minutes = Math.Max(1, (int)Math.Ceiling((s.Opens - now).TotalMinutes));
        return $"Waiting for {InGame.ItemName(s.Line.Item)}'s window at {Eorzea.Format(et.Hours, et.Minutes)} (in {minutes} min).";
    }

    /// <summary>The run's list in GatherBuddy Reborn deleted, and your own lists turned back on.</summary>
    private void RestoreGatherLists()
    {
        if (gatherList != null)
        {
            GatherBuddy.SetListEnabled(gatherList, false);
            GatherBuddy.DeleteList(gatherList);
            gatherList = null;
        }
        if (listsTurnedOff != null)
        {
            foreach (var name in listsTurnedOff) GatherBuddy.SetListEnabled(name, true);
            if (listsTurnedOff.Count > 0) Say(HelperNames.GatherBuddy, $"Your lists are back on: {string.Join(", ", listsTurnedOff)}.");
            listsTurnedOff = null;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Crafting: Artisan's Endurance mode for the syntheses left, watched through your bags.

    private bool craftStarted;
    private bool craftSeenBusy;
    private DateTime craftAskedAt;
    private DateTime craftBegan;
    private DateTime? craftIdleSince;
    private DateTime? craftDoneAt;
    private int craftLastDone;
    private DateTime craftProgressAt;

    /// <summary>How long Artisan may be busy without one more made before the run pauses (a craft takes a minute or two at most).</summary>
    private static readonly TimeSpan CraftStallWait = TimeSpan.FromMinutes(10);

    private void BeginCraft(RunStep step)
    {
        craftStarted = false;
        craftSeenBusy = false;
        craftIdleSince = null;
        craftDoneAt = null;
        craftBegan = DateTime.UtcNow;
        if (!Artisan.Installed) StepProblem("Artisan isn't installed (or is turned off): install it, then Resume; or Skip this step.");
    }

    private void TickCraft(RunStep step)
    {
        var now = DateTime.UtcNow;
        var name = InGame.ItemName(step.Line.Item);
        var have = Have(step.Kind, step.Line);
        var (done, of) = StepMath.Progress(step, l => Have(step.Kind, l));
        if (StepMath.LineDone(step.Kind, step.Line, have))
        {
            // Artisan finishes the craft under way and stops by itself (it was asked for no more than were missing).
            craftDoneAt ??= now;
            if (craftStarted && Artisan.Busy == true && now - craftDoneAt.Value < TimeSpan.FromSeconds(30)) return;
            if (craftStarted && Artisan.Busy == true) Artisan.StopEndurance();
            craftStarted = false;
            Say(HelperNames.Artisan, $"Made {of} × {name}.");
            EndStep();
            return;
        }
        if (Artisan.StopRequested == true)
        {
            StepProblem("Artisan has been asked to stop (by itself, entering a duty, or another plugin). Resume when it may carry on.");
            return;
        }
        if (!craftStarted)
        {
            // One helper at a time: auto-gather off first.
            if (GatherBuddy.Installed && GatherBuddy.Enabled == true && GatherBuddy.SetEnabled(false)) Say(HelperNames.GatherBuddy, "Auto-gather off before crafting.");
            var busy = Artisan.Busy;
            if (busy == null)
            {
                StepProblem("Artisan isn't answering (another version?). Check it in Helpers, then Resume.");
                return;
            }
            if (busy == true)
            {
                Doing = $"Waiting for Artisan to finish what it's doing · {HelperNames.Artisan}";
                if (now - craftBegan > TimeSpan.FromMinutes(3)) StepProblem("Artisan was still busy with something else after 3 minutes. Stop it, then Resume.");
                return;
            }
            var crafts = StepMath.CraftsLeft(step, have);
            if (crafts <= 0)
            {
                EndStep();
                return;
            }
            if (!Artisan.Craft((ushort)step.Recipe, crafts))
            {
                StepProblem("Artisan didn't take the craft (another version?). Check it in Helpers, then Resume.");
                return;
            }
            craftStarted = true;
            craftSeenBusy = false;
            craftIdleSince = null;
            craftAskedAt = now;
            craftLastDone = done;
            craftProgressAt = now;
            Say(HelperNames.Artisan, $"Crafting {crafts} × {name} (recipe {step.Recipe}{(step.Job != null ? $", {step.Job}" : string.Empty)}).");
            Doing = $"Crafting {name} · {HelperNames.Artisan}";
            Touch(true);
            return;
        }
        if (Artisan.Busy == true)
        {
            craftSeenBusy = true;
            craftIdleSince = null;
            if (done != craftLastDone)
            {
                craftLastDone = done;
                craftProgressAt = now;
            }
            else if (now - craftProgressAt > CraftStallWait)
            {
                StepProblem($"Artisan has been busy for {CraftStallWait.TotalMinutes:0} minutes without making another {name} (stuck on a window?). Look at it, then Resume; or Skip.");
                return;
            }
            var doing = $"Crafting {name} ({done} of {of}) · {HelperNames.Artisan}";
            if (Doing != doing)
            {
                Doing = doing;
                Touch();
            }
            return;
        }
        craftIdleSince ??= now;
        if (!craftSeenBusy && now - craftAskedAt > TimeSpan.FromSeconds(20))
        {
            // Nothing made at all: most likely the materials aren't there (the site leaves out what nobody can get).
            FailStep($"Artisan didn't start crafting {name} (no materials for it in your bags, or the job's gear set missing?).");
            return;
        }
        if (craftSeenBusy && now - craftIdleSince.Value > TimeSpan.FromSeconds(4))
            FailStep($"Artisan stopped with {done} of {of} × {name} made (out of materials, or a craft failed).");
    }
}
