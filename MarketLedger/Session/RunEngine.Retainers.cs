using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using MarketLedger.Automation;
using MarketLedger.Core;
using MarketLedger.Game;

namespace MarketLedger.Session;

/// <summary>
/// Retainers, at a summoning bell (Housing Studio's plugin's errand code): taking a withdraw step's lines
/// from each, and giving an entrust step's to one. AutoRetainer has no IPC for either, so it's the game's
/// own windows: the bell's list, the retainer's menu, their bags and each stack's item menu.
/// </summary>
public sealed partial class RunEngine
{
    private void AddWithdraw(RunStep step)
    {
        var lines = step.Lines.Where(l => !StepMath.LineDone(step.Kind, l, Have(step.Kind, l))).ToList();
        // The saddlebags' lines are taken by you (their window isn't one the plugin knows).
        foreach (var l in lines.Where(l => string.Equals(l.Retainer, HoldingsReader.SaddlebagPlace, StringComparison.OrdinalIgnoreCase)))
        {
            var line = l;
            Add($"Taking {line.Amount} × {InGame.ItemName(line.Item)} from your saddlebag", () => new List<Step>
            {
                You($"Take {StepMath.Missing(step.Kind, line, Have(step.Kind, line))} × {InGame.ItemName(line.Item)} out of your saddlebag",
                    () => StepMath.LineDone(step.Kind, line, Have(step.Kind, line))),
            });
        }
        var byRetainer = lines.Where(l => !string.Equals(l.Retainer, HoldingsReader.SaddlebagPlace, StringComparison.OrdinalIgnoreCase))
            .GroupBy(l => l.Retainer ?? string.Empty, StringComparer.OrdinalIgnoreCase).ToList();
        if (byRetainer.Count == 0) return;
        Add("Going to a summoning bell", BellSteps);
        foreach (var g in byRetainer)
        {
            var name = g.Key;
            Add($"Opening {name}'s bags", () => OpenRetainerSteps(() => InGame.RetainerRowByName(name), name));
            foreach (var l in g)
            {
                var line = l;
                Add($"Taking {InGame.ItemName(line.Item)} from {name}", () => WithdrawSteps(step, line, name));
            }
            Add($"Saying goodbye to {name}", () => CloseRetainerSteps(name));
        }
    }

    private void AddEntrust(RunStep step)
    {
        // To the first retainer in the bell's list (the run doesn't name one).
        Add("Going to a summoning bell", BellSteps);
        const string first = "your first retainer";
        Add($"Opening {first}'s bags", () => OpenRetainerSteps(() => InGame.RetainerListKnown ? 0 : -1, first));
        foreach (var l in step.Lines.Where(l => !StepMath.LineDone(step.Kind, l, Have(step.Kind, l))))
        {
            var line = l;
            Add($"Entrusting {InGame.ItemName(line.Item)}", () => EntrustSteps(step, line));
        }
        Add($"Saying goodbye to {first}", () => CloseRetainerSteps(first));
    }

    private List<Step> BellSteps()
    {
        var asked = false;
        var steps = new List<Step>
        {
            // AutoRetainer kept out of the way at the bell, and one of its own cycles going let finish first.
            new Step
            {
                Label = "AutoRetainer keeps out of the way",
                Run = () =>
                {
                    if (!AutoRetainer.Installed) return StepOutcome.Done;
                    if (!asked)
                    {
                        asked = true;
                        AutoRetainer.Hold();
                        if (!AutoRetainer.Busy) return StepOutcome.Done;
                        Say(HelperNames.AutoRetainer, "Busy: waiting for it (15 s at most).");
                    }
                    return AutoRetainer.Busy ? StepOutcome.Waiting : StepOutcome.Done;
                },
                Timeout = TimeSpan.FromSeconds(15),
                OnTimeout = StepStatus.Done,
            },
        };
        var near = InGame.Me is { } me && InGame.Nearest(Bells(), me, 60, UsableKinds) != null;
        if (!near) steps.AddRange(HubSteps(Bells()));
        steps.AddRange(WalkSteps("a summoning bell", () => InGame.Me is { } p ? InGame.Nearest(Bells(), p, 150, UsableKinds)?.Position : null));
        return steps;
    }

    private List<Step> OpenRetainerSteps(Func<int> row, string name)
    {
        DateTime? bagsLoadedAt = null;
        bool MenuOpen() => InGame.MenuEntries()?.Addon == ErrandTable.SelectString;
        return new List<Step>
        {
            UseUntil("the summoning bell", () => InGame.Me is { } me ? InGame.Nearest(Bells(), me, Reach + 2, UsableKinds) : null,
                "The list of retainers", () => GameUi.IsOpen(ErrandTable.RetainerList)),
            new Step
            {
                Label = $"Choose {name} ({ErrandTable.RetainerPick.Describe()})",
                IsAction = true,
                Run = () =>
                {
                    var r = row();
                    if (r < 0) return StepOutcome.Halt($"{name} isn't in the bell's list.");
                    return GameUi.Fire(ErrandTable.RetainerPick, UiCallValues.Of(("index", r))) ? StepOutcome.Done : StepOutcome.Halt("The list of retainers closed.");
                },
                Timeout = TimeSpan.FromSeconds(5),
            },
            Step.Wait($"{name} comes", () => MenuOpen() || GameUi.IsOpen(ErrandTable.Talk), TimeSpan.FromSeconds(8), StepStatus.Done),
            ClickThrough(name, MenuOpen),
            new Step
            {
                Label = $"Choose \"{ErrandTable.RetainerItemsWords[0]}\"",
                IsAction = true,
                Run = () =>
                {
                    if (InGame.MenuEntries() is not { } menu) return StepOutcome.Halt($"{name}'s menu isn't open.");
                    var i = InGame.Find(menu.Entries, ErrandTable.RetainerItemsWords);
                    if (i < 0) return StepOutcome.Halt($"{name}'s menu has no \"{ErrandTable.RetainerItemsWords[0]}\" (it shows: {string.Join(" | ", menu.Entries)}).");
                    return GameUi.Fire(ErrandTable.MenuPick, UiCallValues.Of(("index", i))) ? StepOutcome.Done : StepOutcome.Halt($"{name}'s menu closed.");
                },
            },
            new Step
            {
                Label = $"{name}'s bags open",
                Run = () =>
                {
                    if (!InGame.RetainerBagsOpen) return StepOutcome.Waiting;
                    // And what's in them sent, and 0.3 s from then for all of it.
                    if (!InGame.RetainerPagesLoaded()) return StepOutcome.Waiting;
                    bagsLoadedAt ??= DateTime.UtcNow;
                    return DateTime.UtcNow - bagsLoadedAt.Value >= TimeSpan.FromMilliseconds(300) ? StepOutcome.Done : StepOutcome.Waiting;
                },
                Timeout = WindowWait,
            },
        };
    }

    private List<Step> WithdrawSteps(RunStep step, RunLine line, string retainer)
    {
        var before = 0;
        var menuReopened = false;
        var stack = InGame.StackSize(line.Item);
        var name = InGame.ItemName(line.Item);
        List<Step> Once() => new()
        {
            new Step
            {
                Label = $"Open the menu of {name} in {retainer}'s bags",
                IsAction = true,
                Run = () =>
                {
                    var missing = StepMath.Missing(step.Kind, line, Have(step.Kind, line));
                    if (missing <= 0) return Finished();
                    if (!InGame.RetainerBagsOpen) return StepOutcome.Halt($"{retainer}'s bags closed.");
                    if (InGame.RetainerSlot(line.Item) is not { } slot)
                    {
                        Say(HelperNames.Ledger, $"{retainer} has no more {name} ({missing} still wanted).");
                        return Finished();
                    }
                    var room = InGame.FreeSlots() - Math.Max(0, config.KeepFreeSlots);
                    if (!InGame.IsCrystal(line.Item) && room < MarketRule.NewSlots(InGame.NqCount(line.Item), slot.Quantity, stack))
                        return StepOutcome.Halt($"Your bags are nearly full ({InGame.FreeSlots()} free slots, {config.KeepFreeSlots} kept free): the stack of {slot.Quantity} needs more.");
                    before = InGame.BagCount(line.Item);
                    menuReopened = false;
                    guessByHand = () => InGame.BagCount(line.Item) > before;
                    return InGame.OpenRetainerItemMenu(slot.Container, slot.Slot) ? StepOutcome.Done : StepOutcome.Halt("Couldn't open the item's menu.");
                },
            },
            Step.Wait("The item's menu opens", () => GameUi.IsOpen(UiTable.ContextMenu), TimeSpan.FromSeconds(8)),
            new Step
            {
                Label = $"Choose \"{ErrandTable.RetrieveWords[0]}\" (the whole stack)",
                IsAction = true,
                Key = Guess,
                Timeout = TimeSpan.FromSeconds(8),
                Run = () =>
                {
                    guessByHand = null;
                    if (InGame.BagCount(line.Item) > before)
                    {
                        if (GameUi.IsOpen(UiTable.ContextMenu)) GameUi.Fire(UiTable.MenuCancel);
                        return StepOutcome.Done;
                    }
                    var entries = GameUi.MenuEntries();
                    if (entries == null)
                    {
                        // The menu closed (by hand?) with nothing taken: opened again, once.
                        if (menuReopened) return StepOutcome.Waiting;
                        menuReopened = true;
                        if (InGame.RetainerSlot(line.Item) is not { } again) return StepOutcome.Halt("The item's menu closed, and the item isn't in the retainer's bags any more.");
                        return InGame.OpenRetainerItemMenu(again.Container, again.Slot) ? StepOutcome.Waiting : StepOutcome.Halt("The item's menu closed.");
                    }
                    var i = InGame.Find(entries, ErrandTable.RetrieveWords, ErrandTable.RetrieveQuantityWords);
                    if (i < 0)
                    {
                        GameUi.Fire(UiTable.MenuCancel);
                        return StepOutcome.Halt($"The item's menu has no \"{ErrandTable.RetrieveWords[0]}\" (it shows: {string.Join(" | ", entries)}).");
                    }
                    return GameUi.Fire(UiTable.MenuChoose, UiCallValues.Of(("entry", i))) ? StepOutcome.Done : StepOutcome.Halt("The item's menu closed.");
                },
            },
            new Step
            {
                Label = $"{name} comes to your bags",
                Run = () =>
                {
                    if (GameUi.IsOpen("InputNumeric"))
                    {
                        GameUi.Close("InputNumeric");
                        return StepOutcome.Halt($"The game asked how many {name} to take (crystals do): take them yourself, then Resume.");
                    }
                    var now = InGame.BagCount(line.Item);
                    if (now <= before) return StepOutcome.Waiting;
                    Say(HelperNames.Ledger, $"Took {now - before} × {name} from {retainer}.");
                    if (!StepMath.LineDone(step.Kind, line, Have(step.Kind, line))) queue.InsertNext(Once());
                    return StepOutcome.Done;
                },
                Timeout = TimeSpan.FromSeconds(12),
                TimeoutMessage = $"{name} didn't come to your bags.",
            },
        };
        return Once();
    }

    /// <summary>
    /// Gives whole stacks of the item from your bags to the open retainer until what's left in your bags is
    /// the line's need: the stack's item menu (from your bag window) and "Entrust to Retainer". Both are
    /// guesses, so held for you until trusted.
    /// </summary>
    private List<Step> EntrustSteps(RunStep step, RunLine line)
    {
        var before = 0;
        var name = InGame.ItemName(line.Item);
        List<Step> Once() => new()
        {
            new Step
            {
                Label = $"Open the menu of {name} in your bags",
                IsAction = true,
                Key = Guess,
                Run = () =>
                {
                    var left = StepMath.Missing(step.Kind, line, Have(step.Kind, line));
                    if (left <= 0) return Finished();
                    if (!InGame.RetainerBagsOpen) return StepOutcome.Halt("The retainer's bags closed.");
                    // The biggest stack (entrust takes whole stacks): one bigger than what should go is left for you.
                    if (InGame.BagSlot(line.Item, line.Hq ? true : null) is not { } slot)
                    {
                        Say(HelperNames.Ledger, $"No more {name} in your bags to entrust.");
                        return Finished();
                    }
                    if (slot.Quantity > left)
                    {
                        Say(HelperNames.Ledger, $"The stack of {name} holds {slot.Quantity}, more than the {left} to entrust: left for you.");
                        return Finished();
                    }
                    before = InGame.BagCount(line.Item);
                    guessByHand = () => InGame.BagCount(line.Item) < before;
                    return InGame.OpenBagItemMenu(slot.Container, slot.Slot) ? StepOutcome.Done : StepOutcome.Halt("Couldn't open the item's menu (is your bag window open?).");
                },
            },
            Step.Wait("The item's menu opens", () => GameUi.IsOpen(UiTable.ContextMenu) || InGame.BagCount(line.Item) < before, TimeSpan.FromSeconds(8)),
            new Step
            {
                Label = $"Choose \"{ErrandTable.EntrustWords[0]}\"",
                IsAction = true,
                Key = Guess,
                Timeout = TimeSpan.FromSeconds(8),
                Run = () =>
                {
                    guessByHand = null;
                    if (InGame.BagCount(line.Item) < before)
                    {
                        if (GameUi.IsOpen(UiTable.ContextMenu)) GameUi.Fire(UiTable.MenuCancel);
                        return StepOutcome.Done;
                    }
                    var entries = GameUi.MenuEntries();
                    if (entries == null) return StepOutcome.Halt("The item's menu closed.");
                    var i = InGame.Find(entries, ErrandTable.EntrustWords, ErrandTable.EntrustQuantityWords);
                    if (i < 0)
                    {
                        GameUi.Fire(UiTable.MenuCancel);
                        return StepOutcome.Halt($"The item's menu has no \"{ErrandTable.EntrustWords[0]}\" (it shows: {string.Join(" | ", entries)}).");
                    }
                    return GameUi.Fire(UiTable.MenuChoose, UiCallValues.Of(("entry", i))) ? StepOutcome.Done : StepOutcome.Halt("The item's menu closed.");
                },
            },
            new Step
            {
                Label = $"{name} goes to the retainer",
                Run = () =>
                {
                    if (GameUi.IsOpen("InputNumeric"))
                    {
                        GameUi.Close("InputNumeric");
                        return StepOutcome.Halt("The game asked how many to entrust: that isn't done yet.");
                    }
                    var now = InGame.BagCount(line.Item);
                    if (now >= before) return StepOutcome.Waiting;
                    Say(HelperNames.Ledger, $"Entrusted {before - now} × {name}.");
                    if (!StepMath.LineDone(step.Kind, line, Have(step.Kind, line))) queue.InsertNext(Once());
                    return StepOutcome.Done;
                },
                Timeout = TimeSpan.FromSeconds(12),
                TimeoutMessage = $"{name} didn't leave your bags (the retainer's bags full?).",
            },
        };
        return Once();
    }

    private List<Step> CloseRetainerSteps(string name) => new()
    {
        Step.Act($"Close {name}'s bags", () =>
        {
            foreach (var w in ErrandTable.RetainerInventory) GameUi.Close(w);
            return StepOutcome.Done;
        }),
        Step.Wait($"{name}'s menu", () => InGame.MenuEntries() != null, TimeSpan.FromSeconds(5), StepStatus.Done),
        Step.Act($"Choose \"{ErrandTable.RetainerQuitWords[0]}\"", () =>
        {
            if (InGame.MenuEntries() is not { } menu) return StepOutcome.Done;
            var i = InGame.Find(menu.Entries, ErrandTable.RetainerQuitWords);
            if (i >= 0) GameUi.Fire(ErrandTable.MenuPick, UiCallValues.Of(("index", i)));
            else GameUi.Close(menu.Addon);
            return StepOutcome.Done;
        }),
        Step.Wait($"{name} says goodbye", () => GameUi.IsOpen(ErrandTable.Talk) || GameUi.IsOpen(ErrandTable.RetainerList), TimeSpan.FromSeconds(5), StepStatus.Done),
        ClickThrough(name, () => GameUi.IsOpen(ErrandTable.RetainerList) || !Services.Condition[ConditionFlag.OccupiedSummoningBell]),
        Step.Wait("The list of retainers", () => GameUi.IsOpen(ErrandTable.RetainerList), TimeSpan.FromSeconds(5), StepStatus.Done),
        Step.Act("Close the list of retainers", () =>
        {
            GameUi.Close(ErrandTable.RetainerList);
            return StepOutcome.Done;
        }),
        Step.Wait("Away from the bell", () => !Services.Condition[ConditionFlag.OccupiedSummoningBell], TimeSpan.FromSeconds(8), StepStatus.Done),
        You("Close the retainer's windows", () => !Services.Condition[ConditionFlag.OccupiedSummoningBell], TimeSpan.FromMinutes(1)),
    };
}
