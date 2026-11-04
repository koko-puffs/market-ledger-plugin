using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using MarketLedger.Automation;
using MarketLedger.Core;
using MarketLedger.Game;

namespace MarketLedger.Session;

/// <summary>
/// Buying (Housing Studio's plugin's errand buying): at gil shops, the line's price; on the market
/// board, the cheapest listings at or under the line's <c>maxPrice</c> (<see cref="MarketRule"/>). Never
/// past the gil agreed for the run (<c>maxGil</c>), your market ceiling or your gil, and never into the
/// bag slots you keep free. Every purchase question the game asks is checked against what was chosen
/// (the price, and the item's name at a shop) before it's answered yes; anything else is said no to.
/// </summary>
public sealed partial class RunEngine
{
    private long MaxGil => File?.MaxGil ?? 0;

    private bool Usable(VendorOffer o)
    {
        var aetheryte = o.District != null ? Residential.CityAetheryte(o.District) : InGame.AetheryteOf(o.TerritoryId);
        return aetheryte != 0 && InGame.Attuned(aetheryte);
    }

    // ---------------------------------------------------------------------------------------------
    // Vendors.

    private void AddVendors(RunStep step)
    {
        var lines = step.Lines.Where(l => !StepMath.LineDone(step.Kind, l, Have(step.Kind, l))).ToList();
        if (!vendors.Ready)
        {
            StepProblem("The game's vendors are still being read (a few seconds after loading): Resume in a moment.");
            return;
        }
        var chosen = new List<(RunLine Line, VendorOffer Offer)>();
        var none = new List<string>();
        foreach (var l in lines)
        {
            if (vendors.Choose(l.Item, l.Npc, l.Zone, Usable) is { } o) chosen.Add((l, o));
            else none.Add(InGame.ItemName(l.Item));
        }
        if (none.Count > 0) Say(HelperNames.Ledger, $"No vendor the plugin can get to sells {string.Join(", ", none)} (an aetheryte not attuned?): left for you.");
        // One visit per NPC and place: each of their shops in turn.
        foreach (var g in chosen.GroupBy(c => (c.Offer.NpcId, c.Offer.TerritoryId)))
        {
            var v = g.First().Offer;
            Add($"Going to {v.Where()}", () => VendorTravelSteps(v), HelperNames.Lifestream);
            var first = true;
            foreach (var shop in g.GroupBy(c => c.Offer.ShopId))
            {
                var offer = shop.First().Offer;
                var another = !first;
                Add($"Opening {v.NpcName}'s shop", () => OpenShopSteps(offer, another));
                first = false;
                foreach (var (line, o) in shop)
                    Add($"Buying {InGame.ItemName(line.Item)} from {v.NpcName}", () => BuySteps(step, line, o));
            }
            Add($"Closing {v.NpcName}'s shop", () => new List<Step>
            {
                Step.Act("Close the shop", () =>
                {
                    GameUi.Close(ErrandTable.Shop);
                    GameUi.Close(ErrandTable.SelectIconString);
                    GameUi.Close(ErrandTable.SelectString);
                    return StepOutcome.Done;
                }),
                Step.Wait("The shop closes", () => !GameUi.IsOpen(ErrandTable.Shop), TimeSpan.FromSeconds(3), StepStatus.Done),
            });
        }
    }

    private List<Step> VendorTravelSteps(VendorOffer v)
    {
        var steps = new List<Step>();
        if (v.District != null) steps.AddRange(DistrictSteps(v.District, v.PlaceName));
        else steps.AddRange(TeleportSteps(v.TerritoryId, v.PlaceName.Length > 0 ? v.PlaceName : "the vendor's zone"));
        var at = v.Position!.Value;
        // The one in sight, else the nearest spot of an NPC of that name in this zone (a ward's
        // subdivision has its own merchant, and the two divisions can't be walked between).
        Vector3 Target()
        {
            if (InGame.NpcOrNamed(v.NpcId, v.NpcName, 60) is { } seen) return seen.Position;
            var nearest = vendors.SpotsOf(v.NpcName, InGame.Territory).Select(p => (Vector3?)p).OrderBy(p => InGame.DistanceTo(p!.Value)).FirstOrDefault();
            return nearest ?? at;
        }
        steps.AddRange(WalkSteps(v.NpcName, () => Target(), NpcReach));
        return steps;
    }

    /// <summary>
    /// Talks to the vendor and goes through their menus until their shop opens. <paramref name="another"/>:
    /// another of their shops, after one: the open one is closed first, and they're talked to again unless
    /// their menu came back by itself.
    /// </summary>
    private List<Step> OpenShopSteps(VendorOffer v, bool another)
    {
        var menus = 0;
        var used = DateTime.MinValue;
        var again = 0;
        Step Opens() => new()
        {
            Label = $"{v.NpcName}'s shop opens",
            Run = () =>
            {
                if (used == DateTime.MinValue) used = DateTime.UtcNow;
                if (GameUi.IsOpen(ErrandTable.Shop)) return StepOutcome.Done;
                if (InGame.MenuEntries() != null)
                {
                    if (++menus > 3) return StepOutcome.Halt($"{v.NpcName}'s menus went on and on.");
                    queue.InsertNext(new[] { PickShop(), Opens() });
                    return StepOutcome.Done;
                }
                if (GameUi.IsOpen(ErrandTable.Talk))
                {
                    queue.InsertNext(new[] { ClickThrough(v.NpcName, () => !GameUi.IsOpen(ErrandTable.Talk)), Opens() });
                    return StepOutcome.Done;
                }
                // Nothing opened (the game refused, mid-jump say): talk to them again, twice at most.
                if (DateTime.UtcNow - used > TimeSpan.FromSeconds(2.5) && again < 2 && InGame.Settled && InGame.NpcOrNamed(v.NpcId, v.NpcName, Reach + 3) is { } npc
                    && !InGame.TargetFirst(npc))
                {
                    again++;
                    used = DateTime.UtcNow;
                    InGame.Interact(npc);
                }
                return StepOutcome.Waiting;
            },
            Timeout = WindowWait,
            TimeoutMessage = $"{v.NpcName}'s shop didn't open.",
        };
        Step PickShop() => new()
        {
            Label = $"Choose \"{v.ShopName}\" in {v.NpcName}'s menu",
            IsAction = true,
            Run = () =>
            {
                if (InGame.MenuEntries() is not { } menu) return StepOutcome.Halt($"{v.NpcName}'s menu closed.");
                var i = v.ShopName.Length == 0 ? -1 : menu.Entries.FindIndex(e => string.Equals(e.Trim(), v.ShopName.Trim(), StringComparison.OrdinalIgnoreCase));
                if (i < 0 && v.ShopName.Length > 0)
                    i = menu.Entries.FindIndex(e => e.Trim().Length > 0
                                                    && (e.Contains(v.ShopName.Trim(), StringComparison.OrdinalIgnoreCase) || v.ShopName.Contains(e.Trim(), StringComparison.OrdinalIgnoreCase)));
                if (i < 0) return StepOutcome.Halt($"{v.NpcName}'s menu has no \"{v.ShopName}\" (it shows: {string.Join(" | ", menu.Entries)}).");
                var call = menu.Addon == ErrandTable.SelectIconString ? ErrandTable.IconMenuPick : ErrandTable.MenuPick;
                return GameUi.Fire(call, UiCallValues.Of(("index", i))) ? StepOutcome.Done : StepOutcome.Halt($"{v.NpcName}'s menu closed.");
            },
        };
        if (!another)
            return new List<Step>
            {
                Use(v.NpcName, () => InGame.NpcOrNamed(v.NpcId, v.NpcName, Reach + 3)),
                Opens(),
            };
        var closedAt = DateTime.MinValue;
        return new List<Step>
        {
            Step.Act("Close the last shop", () =>
            {
                GameUi.Close(ErrandTable.Shop);
                return StepOutcome.Done;
            }),
            new Step
            {
                Label = $"The shop closes ({v.NpcName}'s menu may come back)",
                Run = () =>
                {
                    if (GameUi.IsOpen(ErrandTable.Shop)) return StepOutcome.Waiting;
                    if (InGame.MenuEntries() != null) return StepOutcome.Done;
                    if (closedAt == DateTime.MinValue) closedAt = DateTime.UtcNow;
                    return FreeToUse() && DateTime.UtcNow - closedAt > TimeSpan.FromMilliseconds(400) ? StepOutcome.Done : StepOutcome.Waiting;
                },
                Timeout = TimeSpan.FromSeconds(5),
                TimeoutMessage = $"{v.NpcName}'s shop didn't close.",
            },
            new Step
            {
                Label = $"Talk to {v.NpcName} again (unless their menu is up)",
                IsAction = true,
                Run = () =>
                {
                    if (InGame.MenuEntries() != null) return StepOutcome.Done;
                    if (InGame.NpcOrNamed(v.NpcId, v.NpcName, Reach + 3) is not { } npc) return StepOutcome.Halt($"Couldn't find {v.NpcName} within reach. Near you: {InGame.NamesNear(15)}.");
                    if (InGame.TargetFirst(npc)) return StepOutcome.Waiting;
                    return InGame.Interact(npc) ? StepOutcome.Done : StepOutcome.Halt($"Couldn't talk to {v.NpcName}.");
                },
            },
            Opens(),
        };
    }

    private List<Step> BuySteps(RunStep step, RunLine line, VendorOffer v)
    {
        var before = 0;
        long gilBefore = 0;
        var n = 0;
        DateTime? gilWait = null;
        var stack = InGame.StackSize(line.Item);
        var name = InGame.ItemName(line.Item);
        // The run's price for one: the site's (what it agreed), never above it.
        var price = line.Price > 0 ? line.Price : v.Price;
        List<Step> Once() => new()
        {
            new Step
            {
                Label = $"Buy {name} from {v.NpcName} ({ErrandTable.ShopBuy.Describe()})",
                IsAction = true,
                Run = () =>
                {
                    var missing = StepMath.Missing(step.Kind, line, Have(step.Kind, line));
                    if (missing <= 0) return Finished();
                    if (!GameUi.IsOpen(ErrandTable.Shop)) return StepOutcome.Halt($"{v.NpcName}'s shop closed.");
                    if (v.Price > price) return StepOutcome.Halt($"{v.NpcName} sells {name} for {v.Price:N0} gil, more than the run's {price:N0}.");
                    n = Math.Min(missing, Math.Min(MaxPerPurchase, stack));
                    if (MarketRule.VendorWhyNot(v.Price, n, MaxGil, Spent, InGame.Gil()) is { } why) return StepOutcome.Halt($"Not bought: {why}.");
                    var room = InGame.FreeSlots() - Math.Max(0, config.KeepFreeSlots);
                    if (room < MarketRule.NewSlots(InGame.NqCount(line.Item), n, stack)) return StepOutcome.Halt($"Your bags are nearly full ({InGame.FreeSlots()} free slots, {config.KeepFreeSlots} kept free).");
                    var row = InGame.ShopRow(v.ShopId, line.Item);
                    if (row < 0)
                    {
                        Say(HelperNames.Ledger, $"{name} isn't in {v.NpcName}'s list (it may need a quest first): left for you.");
                        return Finished();
                    }
                    before = InGame.BagCount(line.Item);
                    gilBefore = InGame.Gil();
                    Doing = $"Buying {name} from {v.NpcName} · {HelperNames.Ledger}";
                    return GameUi.Fire(ErrandTable.ShopBuy, UiCallValues.Of(("row", row), ("count", n))) ? StepOutcome.Done : StepOutcome.Halt($"{v.NpcName}'s shop closed.");
                },
            },
            Bought(),
        };
        Step Bought() => new()
        {
            Label = $"{name} comes to your bags",
            Run = () =>
            {
                var now = InGame.BagCount(line.Item);
                if (now < before + n)
                {
                    if (GameUi.YesNoText() is { } question && now == before)
                    {
                        // Only a question about this purchase is answered yes: its price and the item's name.
                        if (!MarketRule.QuestionMatches(question, v.Price, n, (long)v.Price * n) || !MarketRule.MentionsItem(question, name))
                        {
                            GameUi.Close(UiTable.YesNo);
                            return StepOutcome.Halt($"The shop asked \"{question}\", not {n} × {name} for {(long)v.Price * n:N0} gil: said no.");
                        }
                        queue.InsertNext(new[] { Yes(question), Bought() });
                        return StepOutcome.Done;
                    }
                    return StepOutcome.Waiting;
                }
                // The gil may go a frame or two after the item comes: waited for (1.5 s), so it's counted.
                gilWait ??= DateTime.UtcNow;
                if (InGame.Gil() >= gilBefore && DateTime.UtcNow - gilWait.Value < TimeSpan.FromSeconds(1.5)) return StepOutcome.Waiting;
                gilWait = null;
                var spent = Math.Max(0, gilBefore - InGame.Gil());
                VendorSpent += spent;
                Say(HelperNames.Ledger, $"Bought {now - before} × {name} for {spent:N0} gil.");
                Touch(true);
                if (spent > (long)v.Price * n) return StepOutcome.Halt($"{name} cost {spent:N0} gil, more than the {(long)v.Price * n:N0} planned.");
                if (!StepMath.LineDone(step.Kind, line, Have(step.Kind, line))) queue.InsertNext(Once());
                return StepOutcome.Done;
            },
            Timeout = TimeSpan.FromSeconds(12),
            TimeoutMessage = $"{name} didn't come to your bags.",
        };
        return Once();
    }

    // ---------------------------------------------------------------------------------------------
    // The market board.

    private void AddMarket(RunStep step)
    {
        var lines = step.Lines.Where(l => !StepMath.LineDone(step.Kind, l, Have(step.Kind, l))).ToList();
        var home = InGame.HomeWorld();
        var here = InGame.CurrentWorld();
        // Your own world's (and "any") first, then each other world.
        var byWorld = lines.GroupBy(l => string.IsNullOrEmpty(l.World) ? here : l.World!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => string.Equals(g.Key, here, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var g in byWorld)
        {
            var world = g.Key;
            Add($"Going to a market board on {world}", () => BoardSteps(world), HelperNames.Lifestream);
            foreach (var l in g)
            {
                var line = l;
                Add($"Buying {InGame.ItemName(line.Item)} on the market board", () => ListingSteps(step, line));
            }
            Add("Closing the market board", () => new List<Step>
            {
                Step.Act("Close the board", () =>
                {
                    GameUi.Close(ErrandTable.ItemSearchResult);
                    GameUi.Close(ErrandTable.ItemSearch);
                    return StepOutcome.Done;
                }),
            });
        }
        if (byWorld.Any(g => !string.Equals(g.Key, home, StringComparison.OrdinalIgnoreCase)) && home.Length > 0)
            Add($"Back to {home}", () => InGame.CurrentWorld() == home ? new List<Step>() : HubSteps(null).Concat(WorldSteps(home)).ToList(), HelperNames.Lifestream);
    }

    private List<Step> BoardSteps(string world)
    {
        var steps = new List<Step>();
        if (InGame.CurrentWorld() != world)
        {
            // A world visit leaves from a city's aetheryte.
            steps.AddRange(HubSteps(null));
            steps.AddRange(WorldSteps(world));
        }
        steps.AddRange(HubSteps(Boards()));
        steps.AddRange(WalkSteps("a market board", () => InGame.Me is { } p ? InGame.Nearest(Boards(), p, 150, UsableKinds)?.Position : null));
        steps.Add(UseUntil("the market board", () => InGame.Me is { } me ? InGame.Nearest(Boards(), me, Reach + 2, UsableKinds) : null,
            "The board's search", () => GameUi.IsOpen(ErrandTable.ItemSearch) || GameUi.IsOpen(ErrandTable.ItemSearchResult)));
        return steps;
    }

    private List<Step> ListingSteps(RunStep step, RunLine line)
    {
        var name = InGame.ItemName(line.Item);
        var budget = MarketRule.LineBudget(line.Amount, line.MaxPrice);
        long lineSpent = 0;
        var before = 0;
        long gilBefore = 0;
        long cost = 0;
        uint unit = 0;
        var quantity = 0;
        var tried = new HashSet<ulong>();
        ulong listing = 0;
        var gone = false;
        var retryPending = false;
        var misses = 0;
        var firedAt = DateTime.MinValue;
        DateTime? gilWait = null;
        StepOutcome Missed(string why)
        {
            gone = true;
            misses++;
            BoardListings.Gone(listing);
            if (misses > 3)
            {
                Say(HelperNames.Ledger, $"{name}: {why}; tried {misses} listings, so it's left for now.");
                return Finished();
            }
            Say(HelperNames.Ledger, $"{name}: {why}. Trying the next listing.");
            retryPending = true;
            return StepOutcome.Done;
        }
        List<Step> Once() => new()
        {
            new Step
            {
                Label = $"Buy a listing of {name} ({ErrandTable.ListingBuy.Describe()})",
                IsAction = true,
                Run = () =>
                {
                    var missing = StepMath.Missing(step.Kind, line, Have(step.Kind, line));
                    if (missing <= 0) return Finished();
                    // A late question from the last listing: no, first.
                    if (GameUi.YesNoText() != null)
                    {
                        GameUi.Close(UiTable.YesNo);
                        return StepOutcome.Waiting;
                    }
                    if (BoardListings.Open(line.Item) is not { } listings)
                        return GameUi.IsOpen(ErrandTable.ItemSearchResult) ? StepOutcome.Waiting : StepOutcome.Halt($"{name}'s listings closed.");
                    gone = false;
                    var gil = InGame.Gil();
                    var runLeft = MarketRule.MarketLeft(MaxGil, Spent, config.MarketCeiling, MarketSpent);
                    string? firstWhy = null;
                    foreach (var l in listings.OrderBy(l => l.UnitPrice))
                    {
                        if (tried.Contains(l.ListingId)) continue;
                        var why = MarketRule.WhyNot(l.UnitPrice, l.Quantity, l.TotalTax, line.MaxPrice, missing, budget - lineSpent, runLeft, gil, line.Hq, l.Hq);
                        if (why != null)
                        {
                            firstWhy ??= why;
                            continue;
                        }
                        var room = InGame.FreeSlots() - Math.Max(0, config.KeepFreeSlots);
                        if (room < MarketRule.NewSlots(l.Hq ? 0 : InGame.NqCount(line.Item), l.Quantity, InGame.StackSize(line.Item)))
                        {
                            firstWhy ??= $"your bags have {Math.Max(0, room)} free slots past the {config.KeepFreeSlots} kept free";
                            continue;
                        }
                        cost = MarketRule.Cost(l.UnitPrice, l.Quantity, l.TotalTax);
                        unit = l.UnitPrice;
                        quantity = l.Quantity;
                        tried.Add(l.ListingId);
                        listing = l.ListingId;
                        before = InGame.BagCount(line.Item);
                        gilBefore = gil;
                        Doing = $"Buying {name} on the market board · {HelperNames.Ledger}";
                        Say(HelperNames.Ledger, $"Buying {l.Quantity} × {name} at {l.UnitPrice:N0} gil each ({cost:N0} with tax).");
                        firedAt = DateTime.UtcNow;
                        return GameUi.Fire(ErrandTable.ListingBuy, UiCallValues.Of(("index", l.Row))) ? StepOutcome.Done : StepOutcome.Halt($"{name}'s listings closed.");
                    }
                    Say(HelperNames.Ledger, $"No listing of {name} to buy here ({firstWhy ?? "none listed"}): {missing} left.");
                    return Finished();
                },
                Timeout = TimeSpan.FromSeconds(15),
                TimeoutMessage = $"{name}'s listings didn't come.",
            },
            new Step
            {
                // No question in 5 s: the listing has gone (bought by someone else first); the next one.
                Label = "The board asks to confirm",
                Timeout = TimeSpan.FromSeconds(8),
                Run = () =>
                {
                    if (GameUi.YesNoText() != null) return StepOutcome.Done;
                    if (DateTime.UtcNow - firedAt < TimeSpan.FromSeconds(5)) return StepOutcome.Waiting;
                    return Missed("the board didn't ask to confirm (sold to someone else first?)");
                },
            },
            new Step
            {
                Label = $"Answer yes (buy {name})",
                IsAction = true,
                Run = () =>
                {
                    if (gone) return StepOutcome.Done;
                    // Only the listing chosen: the question must name its cost (another row's is said no to).
                    var question = GameUi.YesNoText();
                    if (!MarketRule.BoardQuestionMatches(question, quantity, cost, (long)unit * quantity))
                    {
                        GameUi.Close(UiTable.YesNo);
                        return Missed($"the board asked \"{question}\", not the {cost:N0} gil listing chosen: said no");
                    }
                    return GameUi.Fire(UiTable.YesNoYes) ? StepOutcome.Done : StepOutcome.Halt("The yes/no closed.");
                },
            },
            new Step
            {
                Label = $"{name} comes to your bags",
                Run = () =>
                {
                    if (!gone)
                    {
                        var came = InGame.BagCount(line.Item);
                        if (came <= before && DateTime.UtcNow - firedAt < TimeSpan.FromSeconds(10)) return StepOutcome.Waiting;
                        // The gil has gone: it's bought and on its way (a slow server). Never another listing meanwhile.
                        if (came <= before && InGame.Gil() < gilBefore) return StepOutcome.Waiting;
                        if (came <= before && Missed("it didn't come to your bags (sold to someone else first?)").Status != StepStatus.Done) return Finished();
                    }
                    if (gone)
                    {
                        if (retryPending)
                        {
                            retryPending = false;
                            queue.InsertNext(Once());
                        }
                        return StepOutcome.Done;
                    }
                    gilWait ??= DateTime.UtcNow;
                    if (InGame.Gil() >= gilBefore && DateTime.UtcNow - gilWait.Value < TimeSpan.FromSeconds(1.5)) return StepOutcome.Waiting;
                    gilWait = null;
                    var now = InGame.BagCount(line.Item);
                    BoardListings.Gone(listing);
                    var spent = Math.Max(0, gilBefore - InGame.Gil());
                    MarketSpent += spent;
                    lineSpent += spent;
                    Say(HelperNames.Ledger, $"Bought {now - before} × {name} for {spent:N0} gil.");
                    Touch(true);
                    if (spent > cost + 1) return StepOutcome.Halt($"That listing cost {spent:N0} gil, more than the {cost:N0} it showed.");
                    if (Spent > MaxGil) return StepOutcome.Halt($"The run has spent {Spent:N0} gil, past the {MaxGil:N0} agreed.");
                    if (!StepMath.LineDone(step.Kind, line, Have(step.Kind, line))) queue.InsertNext(Once());
                    return StepOutcome.Done;
                },
                Timeout = TimeSpan.FromSeconds(15),
                TimeoutMessage = $"{name} didn't come to your bags (sold to someone else first?).",
            },
        };
        var steps = SearchSteps(line.Item, name);
        steps.AddRange(Once());
        return steps;
    }

    /// <summary>How long a board search's results take to come (the server's, so not a pace).</summary>
    private static readonly TimeSpan BoardResultsWait = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Opens an item's listings: the board searched for its name (as typing it does), its row found in
    /// the results, and opened; what opens is checked to be that item's before anything is bought.
    /// Whatever doesn't work is left to you (search and open it).
    /// </summary>
    private List<Step> SearchSteps(uint itemId, string name)
    {
        var index = -1;
        var picks = 0;
        var searches = 1;
        var pickedAt = DateTime.MinValue;
        DateTime? redoAt = null;
        DateTime? searchedAt = null;
        bool Opened() => GameUi.IsOpen(ErrandTable.ItemSearchResult)
                         && (InGame.Listings(itemId) != null || (DateTime.UtcNow - BoardListings.AskedAt(itemId)).TotalSeconds > 10);
        Step YouOpen(string what)
        {
            BoardListings.Expect(itemId);
            return You($"{what} {name} and open its listings", Opened);
        }
        return new List<Step>
        {
            Step.Act("Close the last item's listings", () =>
            {
                if (GameUi.IsOpen(ErrandTable.ItemSearchResult) && InGame.ListingsItem() != itemId) GameUi.Close(ErrandTable.ItemSearchResult);
                return StepOutcome.Done;
            }),
            Step.Wait("The board's search", () => Opened() || (GameUi.IsOpen(ErrandTable.ItemSearch) && !GameUi.IsOpen(ErrandTable.ItemSearchResult)), WindowWait, StepStatus.Done),
            new Step
            {
                Label = $"Search the board for \"{name}\"",
                IsAction = true,
                Run = () =>
                {
                    if (Opened()) return StepOutcome.Done;
                    if (name.Length == 0 || GameUi.IsOpen(ErrandTable.ItemSearchResult)
                        || !GameUi.FireValues(ErrandTable.ItemSearch, true, ErrandTable.SearchByWords, null, null, name, name, null, null, null))
                    {
                        queue.InsertNext(new[] { YouOpen("Search the board for") });
                        return StepOutcome.Done;
                    }
                    searchedAt = DateTime.UtcNow;
                    return StepOutcome.Done;
                },
            },
            new Step
            {
                Label = $"Find {name} in the results",
                Run = () =>
                {
                    if (Opened() || searchedAt == null) return StepOutcome.Done;
                    index = InGame.SearchResultIndex(name, out _);
                    if (index >= 0) return StepOutcome.Done;
                    if (DateTime.UtcNow - searchedAt.Value < BoardResultsWait) return StepOutcome.Waiting;
                    // Searched by its full name, the item is the first result; whose listings open is checked next.
                    index = 0;
                    return StepOutcome.Done;
                },
            },
            new Step
            {
                Label = $"Open {name}'s listings ({ErrandTable.SearchResultPick.Describe()})",
                IsAction = true,
                Run = () =>
                {
                    if (Opened()) return StepOutcome.Done;
                    if (index < 0) index = 0;
                    BoardListings.Expect(itemId);
                    if (!GameUi.Fire(ErrandTable.SearchResultPick, UiCallValues.Of(("index", index))))
                        queue.InsertNext(new[] { YouOpen("Search the board for") });
                    pickedAt = DateTime.UtcNow;
                    picks = 1;
                    return StepOutcome.Done;
                },
            },
            new Step
            {
                Label = $"{name}'s listings come",
                Run = () =>
                {
                    if (Opened()) return StepOutcome.Done;
                    var now = DateTime.UtcNow;
                    var resultOpen = GameUi.IsOpen(ErrandTable.ItemSearchResult);
                    var title = resultOpen && GameUi.TextValues(ErrandTable.ItemSearchResult).TryGetValue(2, out var t) ? t.Trim() : null;
                    var other = resultOpen ? InGame.ListingsItem() : 0;
                    var wrong = resultOpen && ((title is { Length: > 0 } && !string.Equals(title, name, StringComparison.OrdinalIgnoreCase)) || (other != 0 && other != itemId));
                    if (wrong)
                    {
                        GameUi.Close(ErrandTable.ItemSearchResult);
                        if (searches >= 3)
                        {
                            queue.InsertNext(new[] { YouOpen("In the board's results, find") });
                            return StepOutcome.Done;
                        }
                        redoAt = now + TimeSpan.FromMilliseconds(600);
                        return StepOutcome.Waiting;
                    }
                    if (redoAt is { } r && now >= r)
                    {
                        redoAt = null;
                        searches++;
                        if (!GameUi.FireValues(ErrandTable.ItemSearch, true, ErrandTable.SearchByWords, null, null, name, name, null, null, null))
                        {
                            queue.InsertNext(new[] { YouOpen("Search the board for") });
                            return StepOutcome.Done;
                        }
                        pickedAt = now + BoardResultsWait;
                        picks = 0;
                        return StepOutcome.Waiting;
                    }
                    if (redoAt != null || resultOpen) return StepOutcome.Waiting;
                    // Nothing opened: the pick went before the results were there. Picked again every 1.2 s, three times, then searched again.
                    if (now < pickedAt + TimeSpan.FromMilliseconds(picks == 0 ? 0 : 1200)) return StepOutcome.Waiting;
                    if (picks < 3 && GameUi.IsOpen(ErrandTable.ItemSearch))
                    {
                        BoardListings.Expect(itemId);
                        GameUi.Fire(ErrandTable.SearchResultPick, UiCallValues.Of(("index", index)));
                        picks++;
                        pickedAt = now;
                        return StepOutcome.Waiting;
                    }
                    if (searches < 3 && GameUi.IsOpen(ErrandTable.ItemSearch))
                    {
                        redoAt = now;
                        return StepOutcome.Waiting;
                    }
                    queue.InsertNext(new[] { YouOpen("In the board's results, find") });
                    return StepOutcome.Done;
                },
                Timeout = TimeSpan.FromSeconds(30),
                TimeoutMessage = $"{name}'s listings didn't come.",
            },
        };
    }
}
