using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketLedger.Core;

/// <summary>
/// The counts a run goes by. Every line says how many must be in your bags once its step is done
/// (<see cref="RunLine.Need"/>), and how many the step itself gets (<see cref="RunLine.Amount"/>): so
/// what a step has done is worked out from your bags, and a step started again after a stop picks up
/// where it was. Pure, so it's tested.
/// </summary>
public static class StepMath
{
    /// <summary>
    /// How many are still to come to your bags (or, for an entrust line, still to leave them: its
    /// <see cref="RunLine.Need"/> is what's left in your bags after).
    /// </summary>
    public static int Missing(RunStepKind kind, RunLine line, int have) =>
        kind == RunStepKind.Entrust ? Math.Max(0, have - line.Need) : Math.Max(0, line.Need - have);

    /// <summary>
    /// How many of the line's own amount are done, as your bags say: what's in them past what the step
    /// started from (need less amount), or for an entrust line what's gone from them.
    /// </summary>
    public static int Done(RunStepKind kind, RunLine line, int have)
    {
        var done = kind == RunStepKind.Entrust ? line.Need + line.Amount - have : have - (line.Need - line.Amount);
        return Math.Clamp(done, 0, Math.Max(0, line.Amount));
    }

    public static bool LineDone(RunStepKind kind, RunLine line, int have) => Missing(kind, line, have) == 0;

    /// <summary>A step's progress: its lines' done and amounts together.</summary>
    public static (int Done, int Of) Progress(RunStep step, Func<RunLine, int> have)
    {
        int done = 0, of = 0;
        foreach (var l in step.Lines)
        {
            done += Done(step.Kind, l, have(l));
            of += Math.Max(0, l.Amount);
        }
        return (done, of);
    }

    /// <summary>Every line of the step is where it should be in your bags.</summary>
    public static bool StepDone(RunStep step, Func<RunLine, int> have) => step.Lines.All(l => LineDone(step.Kind, l, have(l)));

    /// <summary>How many one synthesis of a craft step makes (its amount over its crafts, at least 1).</summary>
    public static int Yield(RunStep craft) =>
        craft.Crafts <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(craft.Line.Amount / (double)craft.Crafts));

    /// <summary>
    /// The syntheses still to start for a craft step: enough for what's missing, never more than the
    /// step planned (its materials were planned for that many).
    /// </summary>
    public static int CraftsLeft(RunStep craft, int have)
    {
        var missing = Missing(RunStepKind.Craft, craft.Line, have);
        if (missing <= 0) return 0;
        var left = (int)Math.Ceiling(missing / (double)Yield(craft));
        return Math.Clamp(left, 0, Math.Max(0, craft.Crafts));
    }

    /// <summary>The first step from <paramref name="from"/> that isn't done or skipped, or -1 when none is left.</summary>
    public static int NextStep(IReadOnlyList<StepState> states, int from = 0)
    {
        for (var i = Math.Max(0, from); i < states.Count; i++)
            if (states[i] is not (StepState.Done or StepState.Skipped)) return i;
        return -1;
    }

    /// <summary>Who does a kind of step, as the window and the site say it.</summary>
    public static string By(RunStepKind kind) => kind switch
    {
        RunStepKind.Gather => HelperNames.GatherBuddy,
        RunStepKind.Craft => HelperNames.Artisan,
        RunStepKind.Vendor or RunStepKind.Market => HelperNames.Lifestream + " · " + HelperNames.Ledger,
        _ => HelperNames.Ledger,
    };

    /// <summary>The step's verb: "Take from retainers", "Buy from vendors"…</summary>
    public static string Verb(RunStepKind kind) => kind switch
    {
        RunStepKind.Withdraw => "Take from retainers",
        RunStepKind.Vendor => "Buy from vendors",
        RunStepKind.Market => "Buy on the market board",
        RunStepKind.Gather => "Gather",
        RunStepKind.Craft => "Craft",
        _ => "Entrust to a retainer",
    };

    /// <summary>"Gather Ra'Kaznar Ore and 1 more", "Craft Gemdraught of Strength": a step in a few words.</summary>
    public static string Label(RunStep step, Func<uint, string> name)
    {
        var first = step.Lines.Count == 0 ? string.Empty : name(step.Lines[0].Item);
        var more = step.Lines.Count - 1;
        var what = more > 0 ? $"{first} and {more} more" : first;
        return step.Kind switch
        {
            RunStepKind.Gather => $"Gather {what}",
            RunStepKind.Craft => $"Craft {what}",
            RunStepKind.Withdraw => $"Take {what} from retainers",
            RunStepKind.Vendor => $"Buy {what} from vendors",
            RunStepKind.Market => $"Buy {what} on the market",
            _ => $"Entrust {what}",
        };
    }
}

/// <summary>The helpers' names, as the log and the window say them.</summary>
public static class HelperNames
{
    public const string Ledger = "Market Ledger";
    public const string Artisan = "Artisan";
    public const string GatherBuddy = "GatherBuddy Reborn";
    public const string Vnavmesh = "vnavmesh";
    public const string Lifestream = "Lifestream";
    public const string AutoRetainer = "AutoRetainer";
    public const string AllaganTools = "Allagan Tools";
}

/// <summary>The runs sent from the site and not started, in the order they're offered.</summary>
public static class RunOrder
{
    /// <summary>The oldest sent first; those you said "later" to after the rest, in the order you said it.</summary>
    public static List<T> Inbox<T>(IEnumerable<T> runs, Func<T, Guid> id, Func<T, DateTime> sentAt, IReadOnlyList<Guid> later)
    {
        int Rank(T r)
        {
            var i = -1;
            for (var k = 0; k < later.Count; k++)
                if (later[k] == id(r)) i = k;
            return i;
        }
        return runs.OrderBy(r => Rank(r) >= 0 ? 1 : 0).ThenBy(Rank).ThenBy(sentAt).ToList();
    }
}

/// <summary>A run's log: the newest lines first, at most <see cref="Keep"/> kept.</summary>
public sealed class RunLog
{
    public const int Keep = 200;

    private readonly List<ProgressLog> lines = new();

    public IReadOnlyList<ProgressLog> Lines => lines;

    /// <summary>Goes up with each line.</summary>
    public int Version { get; private set; }

    public void Add(DateTime at, string who, string text)
    {
        // The same line again at once (a wait said each frame) isn't kept twice.
        if (lines.Count > 0 && lines[0].Who == who && lines[0].Text == text && at - lines[0].At < TimeSpan.FromSeconds(30)) return;
        lines.Insert(0, new ProgressLog(at, who, text));
        if (lines.Count > Keep) lines.RemoveRange(Keep, lines.Count - Keep);
        Version++;
    }

    public void Clear()
    {
        lines.Clear();
        Version++;
    }
}

/// <summary>Eorzea time: it runs 3600/175 times as fast as real time, from the same start (1970).</summary>
public static class Eorzea
{
    public const double Speed = 3600.0 / 175.0;

    private const long DayMs = 24L * 60 * 60 * 1000;

    /// <summary>Eorzea time in ms since 1970 (whole numbers: real ms × 3600 / 175).</summary>
    public static long EorzeaMs(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds() * 3600 / 175;

    /// <summary>The real time (UTC) an Eorzea time comes, to the ms after.</summary>
    public static DateTime RealTime(long eorzeaMs) => DateTimeOffset.FromUnixTimeMilliseconds((eorzeaMs * 175 + 3599) / 3600).UtcDateTime;

    /// <summary>The Eorzea hour and minute now.</summary>
    public static (int Hours, int Minutes) Now(DateTime utc)
    {
        var et = EorzeaMs(utc) % DayMs;
        return ((int)(et / 3_600_000), (int)(et / 60_000 % 60));
    }

    /// <summary>"16:00 ET".</summary>
    public static string Format(int hours, int minutes) => $"{hours:00}:{minutes:00} ET";

    /// <summary>
    /// A timed node's window now or next: when it opens and closes (real time, UTC), and whether it's
    /// open now. A window open now gives its own opening (in the past).
    /// </summary>
    public static (DateTime OpensAt, DateTime ClosesAt, bool Open) NextWindow(TimedWindow w, DateTime utc)
    {
        var now = EorzeaMs(utc);
        var dayStart = now - now % DayMs;
        long bestOpen = long.MaxValue, bestClose = 0;
        var duration = Math.Max(1, w.Duration) * 60_000L;
        foreach (var h in w.Spawns)
            for (var d = -1; d <= 1; d++)
            {
                var open = dayStart + d * DayMs + h * 3_600_000L;
                var close = open + duration;
                if (close <= now) continue;
                if (open < bestOpen)
                {
                    bestOpen = open;
                    bestClose = close;
                }
            }
        if (bestOpen == long.MaxValue) return (utc, utc, false);
        return (RealTime(bestOpen), RealTime(bestClose), bestOpen <= now);
    }
}
