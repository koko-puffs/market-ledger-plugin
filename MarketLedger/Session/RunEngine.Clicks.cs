using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using MarketLedger.Automation;
using MarketLedger.Core;
using MarketLedger.Game;

namespace MarketLedger.Session;

/// <summary>
/// The steps the plugin does with its own clicks (taking from retainers, buying, entrusting): each is a
/// list of parts (getting to a bell, opening a retainer, buying one line…), each part a list of small
/// actions run by a <see cref="StepQueue"/>, as Housing Studio's plugin's errands are. Getting about:
/// Lifestream's teleports, aethernet and world visits, vnavmesh's walking.
/// </summary>
public sealed partial class RunEngine
{
    private readonly record struct Part(string Title, Func<List<Step>> Steps, string Who);

    private readonly List<Part> segments = new();
    private int segment = -1;

    /// <summary>While a click not yet seen working is held: true once you've done it by hand (the item moved), so the run carries on.</summary>
    private Func<bool>? guessByHand;

    private void Add(string title, Func<List<Step>> steps, string who = HelperNames.Ledger) => segments.Add(new Part(title, steps, who));

    private void BeginClicks(RunStep step)
    {
        segments.Clear();
        segment = -1;
        if (!Nav.Installed || !Travel.Installed)
        {
            StepProblem($"{(Nav.Installed ? "Lifestream" : "vnavmesh")} isn't installed (or is turned off): install it, then Resume; or Skip this step.");
            return;
        }
        // One helper at a time: gathering and crafting have stopped before the plugin's own clicks.
        if (GatherBuddy.Installed && GatherBuddy.Enabled == true && GatherBuddy.SetEnabled(false)) Say(HelperNames.GatherBuddy, "Auto-gather off.");
        switch (step.Kind)
        {
            case RunStepKind.Withdraw:
                AddWithdraw(step);
                break;
            case RunStepKind.Vendor:
                AddVendors(step);
                break;
            case RunStepKind.Market:
                AddMarket(step);
                break;
            default:
                AddEntrust(step);
                break;
        }
        if (Status == RunStatus.Paused) return;
        NextPart();
    }

    private void NextPart()
    {
        segment++;
        if (segment >= segments.Count)
        {
            ClicksDone();
            return;
        }
        var p = segments[segment];
        Say(p.Who, $"{p.Title}.");
        Doing = $"{p.Title} · {p.Who}";
        Touch();
        queue.Begin(p.Steps(), WindowCleanup());
    }

    /// <summary>Every part has run: the step's done when your bags say so, else the run pauses saying what's short.</summary>
    private void ClicksDone()
    {
        if (Current is not { } step) return;
        var short_ = step.Lines.Where(l => !StepMath.LineDone(step.Kind, l, Have(step.Kind, l))).ToList();
        if (short_.Count == 0)
        {
            EndStep();
            return;
        }
        var words = string.Join(", ", short_.Select(l => $"{StepMath.Missing(step.Kind, l, Have(step.Kind, l))} × {InGame.ItemName(l.Item)}"));
        StepProblem(step.Kind switch
        {
            RunStepKind.Market => $"Not bought: {words} (none listed at or under the run's price, or the gil agreed ran out). Buy them yourself and Resume, or Skip.",
            RunStepKind.Vendor => $"Not bought: {words}. Buy them yourself and Resume, or Skip.",
            RunStepKind.Withdraw => $"Not taken: {words}. Take them yourself and Resume, or Skip.",
            _ => $"Still in your bags: {words}. Entrust them yourself and Resume, or Skip.",
        });
    }

    private void TickClicks(RunStep step)
    {
        // Clicks not yet seen working wait for you (Resume) until you trust them.
        queue.StepByStep = !config.TrustClicks && queue.Current is { IsAction: true, Key: Guess };
        if (queue.State == QueueState.AwaitingContinue)
        {
            // Done by hand meanwhile (the item moved), or the new clicks trusted since: carried on, as Resume would.
            if (config.TrustClicks)
            {
                guessByHand = null;
                queue.Continue();
            }
            else if (guessByHand?.Invoke() == true)
            {
                guessByHand = null;
                Say(HelperNames.Ledger, "You did that by hand: carrying on.");
                queue.Continue();
            }
            else
            {
                var words = $"Next: {queue.Current?.Label}. This click hasn't been seen working in the game yet: watch it, and press Resume to let it go (or do it yourself). Settings → Trust the new clicks stops this.";
                if (!HeldForYou || Message != words)
                {
                    Status = RunStatus.Waiting;
                    HeldForYou = true;
                    Message = words;
                    Touch(true);
                }
                return;
            }
        }
        switch (queue.State)
        {
            case QueueState.Running:
                queue.Tick();
                var you = queue.Current?.Label is { } l && l.StartsWith("You:", StringComparison.Ordinal) && queue.Elapsed > TimeSpan.FromSeconds(0.5);
                if (you && (Status != RunStatus.Waiting || HeldForYou))
                {
                    Status = RunStatus.Waiting;
                    HeldForYou = false;
                    Message = queue.Current!.Label["You:".Length..].Trim();
                    Touch(true);
                }
                else if (!you && Status == RunStatus.Waiting)
                {
                    Status = RunStatus.Running;
                    HeldForYou = false;
                    Message = null;
                    Touch(true);
                }
                if (queue.State == QueueState.Finished) NextPart();
                else if (queue.State is QueueState.Stopped or QueueState.Skipped) StepProblem(queue.Message ?? "A step didn't work.");
                return;
            case QueueState.AwaitingContinue:
                return;
            case QueueState.Finished:
            case QueueState.Idle:
                NextPart();
                return;
            default:
                StepProblem(queue.Message ?? "A step didn't work.");
                return;
        }
    }

    private List<Step> WindowCleanup() => new()
    {
        new Step
        {
            Label = "Close the windows the run opened",
            Run = () =>
            {
                Nav.Stop();
                walkTarget = null;
                CloseWindows();
                return StepOutcome.Done;
            },
        },
    };

    // ---------------------------------------------------------------------------------------------
    // Objects by name.

    private static readonly List<string> BellNames = new();
    private static readonly List<string> BoardNames = new();

    private static IReadOnlyCollection<string> Bells()
    {
        if (BellNames.Count == 0) BellNames.AddRange(ErrandTable.SummoningBellWords.Append(InGame.EObjName(ErrandTable.SummoningBellRow)).Where(n => n.Length > 0).Distinct());
        return BellNames;
    }

    private static IReadOnlyCollection<string> Boards()
    {
        if (BoardNames.Count == 0) BoardNames.AddRange(ErrandTable.MarketBoardWords.Append(InGame.EObjName(ErrandTable.MarketBoardRow)).Where(n => n.Length > 0).Distinct());
        return BoardNames;
    }

    private static readonly ObjectKind[] UsableKinds = { ObjectKind.EventObj, ObjectKind.HousingEventObject };

    // ---------------------------------------------------------------------------------------------
    // Getting about: teleports, worlds, walking.

    /// <summary>
    /// To Limsa Lominsa's Hawkers' Alley, where the boards and bells are: nothing when you're there with
    /// one of <paramref name="near"/> in sight, or (null) at a city aetheryte a world visit leaves from.
    /// </summary>
    private List<Step> HubSteps(IReadOnlyCollection<string>? near) => new()
    {
        Step.Act("To a market city", () =>
        {
            var city = InGame.Hub(0);
            if (near == null)
            {
                if (!InGame.AtAWorldVisitAetheryte) queue.InsertNext(TeleportSteps(city.AetheryteTerritory, city.Name));
                return StepOutcome.Done;
            }
            if (InGame.AtABoardCity && InGame.Me is { } me && InGame.Nearest(near, me, 150, UsableKinds) != null) return StepOutcome.Done;
            var steps = new List<Step>();
            if (InGame.Territory != city.BoardTerritory)
            {
                if (InGame.Territory != city.AetheryteTerritory) steps.AddRange(TeleportSteps(city.AetheryteTerritory, city.Name));
                if (city.Aethernet != null) steps.AddRange(AethernetSteps(city.Aethernet, city.BoardTerritory, city.BoardPlace));
            }
            queue.InsertNext(steps);
            return StepOutcome.Done;
        }),
    };

    private List<Step> AethernetSteps(string shard, uint territory, string where)
    {
        var firstTry = DateTime.MinValue;
        var lastTry = DateTime.MinValue;
        var asks = 0;
        return new List<Step>
        {
            new Step
            {
                Label = $"Aethernet to {shard} (Lifestream)",
                IsAction = true,
                Run = () =>
                {
                    if (InGame.Territory == territory) return StepOutcome.Done;
                    var now = DateTime.UtcNow;
                    if (firstTry == DateTime.MinValue) firstTry = now;
                    if (InGame.Settled && !Travel.Busy && now - lastTry > TimeSpan.FromSeconds(2))
                    {
                        lastTry = now;
                        travelling = true;
                        if (Travel.Aethernet(shard)) return StepOutcome.Done;
                        travelling = false;
                    }
                    return now - firstTry > TimeSpan.FromSeconds(20)
                        ? StepOutcome.Halt($"Lifestream wouldn't take the aethernet to {shard} (asked for 20 s).")
                        : StepOutcome.Waiting;
                },
                Timeout = TimeSpan.FromSeconds(25),
            },
            new Step
            {
                Label = $"Arrive at {where}",
                Run = () =>
                {
                    if (InGame.Territory != territory && Again(ref lastTry, ref asks, () => Travel.Aethernet(shard)) is { } stop) return stop;
                    if (InGame.Territory != territory || !InGame.Settled || Travel.Busy) return StepOutcome.Waiting;
                    Arrived();
                    return StepOutcome.Done;
                },
                Timeout = TravelWait,
                TimeoutMessage = $"Didn't arrive at {where} within {TravelWait.TotalMinutes:0} minutes.",
            },
        };
    }

    private List<Step> TeleportSteps(uint territory, string where)
    {
        var aetheryte = InGame.AetheryteOf(territory);
        var firstTry = DateTime.MinValue;
        var lastTry = DateTime.MinValue;
        var asks = 0;
        return new List<Step>
        {
            new Step
            {
                Label = $"Teleport to {where} (Lifestream, aetheryte {aetheryte})",
                IsAction = true,
                Run = () =>
                {
                    if (InGame.Territory == territory) return StepOutcome.Done;
                    if (aetheryte == 0) return StepOutcome.Halt($"No aetheryte for {where} is known.");
                    var now = DateTime.UtcNow;
                    if (firstTry == DateTime.MinValue) firstTry = now;
                    // Lifestream says no while it (or you) is still busy: asked again every 2 s for 20 s.
                    if (InGame.Settled && !Travel.Busy && now - lastTry > TimeSpan.FromSeconds(2))
                    {
                        lastTry = now;
                        travelling = true;
                        Doing = $"On the way to {where} · {HelperNames.Lifestream}";
                        if (Travel.Teleport(aetheryte))
                        {
                            Say(HelperNames.Lifestream, $"Teleporting to {where}.");
                            return StepOutcome.Done;
                        }
                        travelling = false;
                    }
                    return now - firstTry > TimeSpan.FromSeconds(20)
                        ? StepOutcome.Halt($"Lifestream wouldn't teleport to {where} (asked for 20 s; is it installed and up to date?).")
                        : StepOutcome.Waiting;
                },
                Timeout = TimeSpan.FromSeconds(25),
            },
            new Step
            {
                Label = $"Arrive in {where}",
                Run = () =>
                {
                    if (InGame.Territory != territory && Again(ref lastTry, ref asks, () => Travel.Teleport(aetheryte)) is { } stop) return stop;
                    if (InGame.Territory != territory || !InGame.Settled || Travel.Busy) return StepOutcome.Waiting;
                    Arrived();
                    return StepOutcome.Done;
                },
                Timeout = TravelWait,
                TimeoutMessage = $"Didn't arrive in {where} within {TravelWait.TotalMinutes:0} minutes.",
            },
        };
    }

    /// <summary>To a ward of a residential district (Lifestream: the city's aetheryte, then its Residential District Aethernet, to ward 1).</summary>
    private List<Step> DistrictSteps(string district, string name)
    {
        var city = Residential.CityAetheryte(district);
        var firstTry = DateTime.MinValue;
        var lastTry = DateTime.MinValue;
        var asks = 0;
        var arrivedAt = DateTime.MinValue;
        bool There() => Residential.DistrictOf(InGame.TerritoryName(InGame.Territory)) == district;
        bool Ask() => Travel.GoToWard(InGame.CurrentWorldId(), city);
        return new List<Step>
        {
            new Step
            {
                Label = $"To {name} (Lifestream: the city's Residential District Aethernet, ward 1)",
                IsAction = true,
                Run = () =>
                {
                    if (There()) return StepOutcome.Done;
                    if (city == 0) return StepOutcome.Halt($"No way to {name} is known.");
                    var now = DateTime.UtcNow;
                    if (firstTry == DateTime.MinValue) firstTry = now;
                    if (InGame.Settled && !Travel.Busy && now - lastTry > TimeSpan.FromSeconds(2))
                    {
                        lastTry = now;
                        travelling = true;
                        if (Ask()) return StepOutcome.Done;
                        travelling = false;
                    }
                    return now - firstTry > TimeSpan.FromSeconds(20)
                        ? StepOutcome.Halt($"Lifestream wouldn't go to {name} (asked for 20 s; is it installed and up to date?).")
                        : StepOutcome.Waiting;
                },
                Timeout = TimeSpan.FromSeconds(25),
            },
            new Step
            {
                Label = $"Arrive in {name}",
                Run = () =>
                {
                    if (!There() && Again(ref lastTry, ref asks, Ask, 20) is { } stop) return stop;
                    if (!There() || !InGame.Settled)
                    {
                        arrivedAt = DateTime.MinValue;
                        return StepOutcome.Waiting;
                    }
                    // Lifestream walks on to the address's plot after the aethernet: stopped a moment after
                    // arriving, so the vendor is walked to instead.
                    if (arrivedAt == DateTime.MinValue) arrivedAt = DateTime.UtcNow;
                    if (DateTime.UtcNow - arrivedAt < TimeSpan.FromMilliseconds(1500)) return StepOutcome.Waiting;
                    if (Travel.Busy) Travel.Abort();
                    Arrived();
                    return StepOutcome.Done;
                },
                Timeout = TravelWait,
                TimeoutMessage = $"Didn't arrive in {name} within {TravelWait.TotalMinutes:0} minutes.",
            },
        };
    }

    private List<Step> WorldSteps(string world)
    {
        var lastTry = DateTime.MinValue;
        var asks = 0;
        return new List<Step>
        {
            Step.Act($"Go to {world} (Lifestream)", () =>
            {
                if (InGame.CurrentWorld() == world) return StepOutcome.Done;
                // Only a world on this data centre: Lifestream's world change would cross to another data
                // centre too, which a run never asks for (and one it can't say about isn't gone to).
                if (Travel.CanVisit(world) != true) return StepOutcome.Halt($"Lifestream doesn't say {world} is on your data centre: not visited.");
                travelling = true;
                lastTry = DateTime.UtcNow;
                Doing = $"Visiting {world} · {HelperNames.Lifestream}";
                Say(HelperNames.Lifestream, $"Visiting {world}.");
                return Travel.ChangeWorld(world) ? StepOutcome.Done : StepOutcome.Halt($"Lifestream wouldn't go to {world}.");
            }),
            new Step
            {
                Label = $"Arrive on {world}",
                Run = () =>
                {
                    if (InGame.CurrentWorld() != world && Again(ref lastTry, ref asks, () => Travel.CanVisit(world) == true && Travel.ChangeWorld(world), 25) is { } stop) return stop;
                    if (InGame.CurrentWorld() != world || !InGame.Settled || Travel.Busy) return StepOutcome.Waiting;
                    Arrived();
                    return StepOutcome.Done;
                },
                Timeout = WorldWait,
                TimeoutMessage = $"Didn't arrive on {world} within {WorldWait.TotalMinutes:0} minutes.",
            },
        };
    }

    /// <summary>Travel is over: zone changes count as unexpected again.</summary>
    private void Arrived()
    {
        travelling = false;
        lastTerritory = InGame.Territory;
    }

    /// <summary>
    /// Travel asked for that never started (settled, Lifestream idle and still not there
    /// <paramref name="afterSeconds"/> s after): asked again, twice, then stopped. Null: carry on waiting.
    /// </summary>
    private StepOutcome? Again(ref DateTime lastTry, ref int asks, Func<bool> ask, int afterSeconds = 12)
    {
        var now = DateTime.UtcNow;
        if (lastTry == DateTime.MinValue) lastTry = now;
        if (!InGame.Settled || Travel.Busy || InGame.Moving || now - lastTry < TimeSpan.FromSeconds(afterSeconds)) return null;
        if (asks >= 2) return StepOutcome.Halt("Lifestream didn't take you there (asked three times).");
        asks++;
        lastTry = now;
        travelling = true;
        Say(HelperNames.Lifestream, "Not on the way yet: asking again.");
        ask();
        return null;
    }

    /// <summary>Walks (vnavmesh) to within <paramref name="within"/> yalms of a spot, found when the walk starts.</summary>
    /// <param name="talkFrom">
    /// When vnavmesh can't get that close (an NPC behind a counter, a board behind a rail), stopping within this
    /// many yalms across (and 2.5 up or down) is close enough to talk to it or use it from.
    /// </param>
    private List<Step> WalkSteps(string what, Func<Vector3?> where, float within = Reach, float talkFrom = TalkFrom)
    {
        var started = DateTime.MinValue;
        var tries = 0;
        Vector3? to = null;
        var mounting = false;
        var sprintTried = DateTime.MinValue;
        var gotDown = false;
        var downAt = DateTime.MinValue;
        var movedFrom = Vector3.Zero;
        var movedAt = DateTime.MinValue;
        DateTime? restartAt = null;
        void SayIt(string s) => Say(HelperNames.Ledger, s.Trim());
        return new List<Step>
        {
            WaitFor("vnavmesh has this zone's map", () => Nav.Ready, TimeSpan.FromSeconds(90),
                "vnavmesh's map of this zone wasn't ready in 90 s (is vnavmesh installed and on?)."),
            new Step
            {
                Label = "Mount up, or sprint",
                IsAction = true,
                Run = () =>
                {
                    mounting = false;
                    gotDown = false;
                    to = where();
                    if (to == null) return StepOutcome.Done;
                    var d = InGame.DistanceTo(to.Value);
                    if (d <= within) return StepOutcome.Done;
                    mounting = Movement.SpeedUp(d, true, SayIt);
                    sprintTried = DateTime.UtcNow;
                    return StepOutcome.Done;
                },
            },
            new Step
            {
                Label = "On the mount",
                Timeout = TimeSpan.FromSeconds(5),
                OnTimeout = StepStatus.Done,
                Run = () => !mounting || (Movement.Mounted && InGame.Settled) ? StepOutcome.Done : StepOutcome.Waiting,
            },
            Step.Act($"Walk to {what} (vnavmesh)", () =>
            {
                to = where();
                if (to == null) return StepOutcome.Halt($"Couldn't find {what} here. Near you: {InGame.NamesNear(40)}.");
                if (InGame.DistanceTo(to.Value) <= within) return StepOutcome.Done;
                // Up to within reach: the path ends there, in front of a counter rather than round it.
                if (!Nav.MoveCloseTo(to.Value, within * 0.8f)) return StepOutcome.Halt($"vnavmesh wouldn't walk to {what}.");
                walkTarget = to;
                started = DateTime.UtcNow;
                movedAt = started;
                movedFrom = InGame.Me ?? Vector3.Zero;
                tries = 1;
                Doing = $"Walking to {what} · {HelperNames.Vnavmesh}";
                return StepOutcome.Done;
            }),
            new Step
            {
                Label = $"Arrive at {what}",
                Run = () =>
                {
                    if (to == null) return StepOutcome.Done;
                    var d = InGame.DistanceTo(to.Value);
                    if (d <= within)
                    {
                        Nav.Stop();
                        walkTarget = null;
                        return StepOutcome.Done;
                    }
                    if (!Movement.Mounted && d > 15 && DateTime.UtcNow - sprintTried > TimeSpan.FromSeconds(3))
                    {
                        sprintTried = DateTime.UtcNow;
                        Movement.TrySprint(SayIt);
                    }
                    // Walking (a path found, not still being worked out) but not moved half a yalm in 12 s
                    // (caught on a wall or a rail): stopped, and asked again a moment later, as stopping short is.
                    // Moved, not closer: a detour round a building gets no closer for a while.
                    var now = DateTime.UtcNow;
                    if (restartAt is { } again)
                    {
                        // (And the zone's map ready again, should the stop have had vnavmesh reload it.)
                        if (now < again || !Nav.Ready) return StepOutcome.Waiting;
                        restartAt = null;
                    }
                    else
                    {
                        var me = InGame.Me;
                        if (!Nav.Walking || me is not { } pos || Vector3.Distance(pos, movedFrom) > 0.5f)
                        {
                            movedFrom = me ?? movedFrom;
                            movedAt = now;
                        }
                        if (Nav.Walking && now - movedAt > TimeSpan.FromSeconds(12))
                        {
                            SayIt($"Not moving towards {what} for 12 s ({d:0} yalms away): walking there again.");
                            Nav.Stop();
                            restartAt = now + TimeSpan.FromMilliseconds(300);
                            return StepOutcome.Waiting;
                        }
                        if (Nav.Busy || now - started < TimeSpan.FromSeconds(2)) return StepOutcome.Waiting;
                    }
                    // Stopped short, but near enough to talk to it from (an NPC behind a counter, whose own spot the
                    // floor doesn't reach: it was walked at twice, then the run stopped).
                    if (InGame.Me is { } here && CloseEnough(here, to.Value, talkFrom))
                    {
                        SayIt($"vnavmesh can't get closer to {what} than {d:0.0} yalms (behind a counter?): close enough, from here.");
                        Nav.Stop();
                        walkTarget = null;
                        return StepOutcome.Done;
                    }
                    // Stopped short: once more, to the nearest floor it can reach by it (the front of a counter),
                    // then it's no way through.
                    if (tries >= 2)
                    {
                        walkTarget = null;
                        return StepOutcome.Halt($"vnavmesh couldn't find a way to {what} (it stopped {d:0} yalms away).");
                    }
                    tries++;
                    started = now;
                    movedAt = now;
                    if (Nav.NearestReachable(to.Value, talkFrom, 2.5f) is { } front && CloseEnough(front, to.Value, talkFrom))
                    {
                        SayIt($"Stopped {d:0} yalms from {what}: walking to the nearest floor that reaches it, {Vector3.Distance(front, to.Value):0.0} yalms from it.");
                        return Nav.MoveCloseTo(front, 0.5f) ? StepOutcome.Waiting : StepOutcome.Halt($"vnavmesh wouldn't walk to {what}.");
                    }
                    return Nav.MoveCloseTo(to.Value, within * 0.8f) ? StepOutcome.Waiting : StepOutcome.Halt($"vnavmesh wouldn't walk to {what}.");
                },
                Timeout = WalkWait,
                TimeoutMessage = $"Didn't get to {what} within {WalkWait.TotalMinutes:0} minutes.",
            },
            // Off the mount before using something, and landed: the game uses nothing while the dismount's jump lasts.
            new Step
            {
                Label = "Get down from the mount",
                Timeout = TimeSpan.FromSeconds(5),
                OnTimeout = StepStatus.Done,
                Run = () =>
                {
                    if (Movement.Mounted)
                    {
                        if (!gotDown && Movement.GetDown(SayIt))
                        {
                            gotDown = true;
                            downAt = DateTime.UtcNow;
                        }
                        return StepOutcome.Waiting;
                    }
                    if (!gotDown) return Movement.Landed ? StepOutcome.Done : StepOutcome.Waiting;
                    return Movement.Landed && DateTime.UtcNow - downAt > TimeSpan.FromMilliseconds(900) ? StepOutcome.Done : StepOutcome.Waiting;
                },
            },
        };
    }

    /// <summary>You're free to use something: landed, and the game not busy with a bell, an event or a menu still closing.</summary>
    private static bool FreeToUse()
    {
        var c = Services.Condition;
        return Movement.Landed && !c[ConditionFlag.OccupiedSummoningBell] && !c[ConditionFlag.OccupiedInEvent]
               && !c[ConditionFlag.OccupiedInQuestEvent] && !c[ConditionFlag.Occupied] && !c[ConditionFlag.Occupied30]
               && !c[ConditionFlag.Occupied33] && !c[ConditionFlag.Occupied38] && !c[ConditionFlag.Occupied39] && !c[ConditionFlag.Casting];
    }

    /// <summary>Uses something (a bell, a board) once you're free to, and waits for its window; used again every 2.5 s it hasn't opened, four times at most.</summary>
    private Step UseUntil(string what, Func<IGameObject?> find, string window, Func<bool> opened)
    {
        var uses = 0;
        var usedAt = DateTime.MinValue;
        return new Step
        {
            Label = $"Use {what} ({window} opens)",
            IsAction = true,
            Timeout = TimeSpan.FromSeconds(20),
            TimeoutMessage = $"{window} didn't open.",
            Run = () =>
            {
                if (opened()) return StepOutcome.Done;
                if (uses > 0 && DateTime.UtcNow - usedAt < TimeSpan.FromSeconds(2.5)) return StepOutcome.Waiting;
                if (!FreeToUse()) return StepOutcome.Waiting;
                if (uses >= 4) return StepOutcome.Halt($"{window} didn't open (used {what} {uses} times).");
                if (find() is not { } o) return StepOutcome.Halt($"Couldn't find {what} within reach. Near you: {InGame.NamesNear(15)}.");
                if (InGame.TargetFirst(o)) return StepOutcome.Waiting;
                uses++;
                usedAt = DateTime.UtcNow;
                InGame.Interact(o);
                return StepOutcome.Waiting;
            },
        };
    }

    /// <summary>Talks to (or uses) the object <paramref name="find"/> finds, near you.</summary>
    private static Step Use(string what, Func<IGameObject?> find) => new()
    {
        Label = $"Use {what}",
        IsAction = true,
        Run = () =>
        {
            var o = find();
            if (o == null) return StepOutcome.Halt($"Couldn't find {what} within reach. Near you: {InGame.NamesNear(15)}.");
            if (InGame.TargetFirst(o)) return StepOutcome.Waiting;
            return InGame.Interact(o) ? StepOutcome.Done : StepOutcome.Halt($"Couldn't use {what}.");
        },
    };

    private static Step WaitFor(string label, Func<bool> until, TimeSpan timeout, string message, StepStatus onTimeout = StepStatus.Stop) => new()
    {
        Label = label,
        Run = () => until() ? StepOutcome.Done : StepOutcome.Waiting,
        Timeout = timeout,
        OnTimeout = onTimeout,
        TimeoutMessage = message,
    };

    /// <summary>Waits for you to do something (only if it isn't done already); the run says it's waiting for you.</summary>
    private static Step You(string what, Func<bool> done, TimeSpan? timeout = null) => new()
    {
        Label = $"You: {what}",
        Run = () => done() ? StepOutcome.Done : StepOutcome.Waiting,
        Timeout = timeout ?? YouWait,
        TimeoutMessage = $"Waited {(timeout ?? YouWait).TotalMinutes:0} minutes for you to {what[..1].ToLowerInvariant()}{what[1..]}.",
    };

    /// <summary>Clicks through an NPC's (or a retainer's) words until <paramref name="done"/>, a click every half second; if they don't move on, you're asked to.</summary>
    private Step ClickThrough(string who, Func<bool> done)
    {
        var clicks = 0;
        var next = DateTime.MinValue;
        DateTime? began = null;
        return new Step
        {
            Label = $"Click through what {who} says ({ErrandTable.TalkNext.Describe()})",
            Run = () =>
            {
                if (done()) return StepOutcome.Done;
                var now = DateTime.UtcNow;
                began ??= now;
                if (clicks >= 15 || now - began.Value > TimeSpan.FromSeconds(15))
                {
                    queue.InsertNext(new[] { You($"Click through what {who} says", done, TimeSpan.FromMinutes(1)) });
                    return StepOutcome.Done;
                }
                if (!GameUi.IsOpen(ErrandTable.Talk) || now < next) return StepOutcome.Waiting;
                clicks++;
                next = now + TimeSpan.FromMilliseconds(500);
                GameUi.Fire(ErrandTable.TalkNext);
                return StepOutcome.Waiting;
            },
            Timeout = TimeSpan.FromMinutes(2),
            TimeoutMessage = $"{who}'s words didn't end.",
        };
    }

    /// <summary>Answers the game's yes (a purchase checked before), only while it's still that question.</summary>
    private static Step Yes(string question) => new()
    {
        Label = $"Answer yes to \"{question}\"",
        IsAction = true,
        Run = () =>
        {
            // A moment has gone by since it was checked (the gap between clicks): the window may be another's now.
            if (GameUi.YesNoText() != question)
            {
                GameUi.Close(UiTable.YesNo);
                return StepOutcome.Halt("The question changed before it was answered: said no.");
            }
            return GameUi.Fire(UiTable.YesNoYes) ? StepOutcome.Done : StepOutcome.Halt("The yes/no closed.");
        },
    };

    /// <summary>The part has nothing left to do: its remaining steps are passed.</summary>
    private StepOutcome Finished()
    {
        queue.FinishAfterThis();
        return StepOutcome.Done;
    }
}
