using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MarketLedger.Core;

// The run file: what the plugin does, step by step, as the site plans it. It mirrors the site's
// `src/features/market-ledger/model/types.ts` (RunFile, RunStep, RunLine, RunProgress, RunStatus) in
// camelCase JSON: change both together.

/// <summary>Where a run is, as the site and the plugin say it (<c>RunStatus</c> in types.ts).</summary>
public enum RunStatus
{
    Draft,
    Sent,
    Accepted,
    Running,
    Paused,

    /// <summary>For a timed node, or for you (a question in the game).</summary>
    Waiting,
    Done,
    Stopped,
    Failed,
}

/// <summary>Where one step is (<c>StepState</c> in types.ts).</summary>
public enum StepState
{
    Waiting,
    Running,
    Done,
    Skipped,
    Failed,
}

/// <summary>What the site asks of a run in hand (<c>RunCommand</c> in types.ts).</summary>
public enum RunCommand
{
    Pause,
    Resume,
    Stop,
    Skip,
}

/// <summary>The six kinds of step, in the order the site plans them.</summary>
public enum RunStepKind
{
    Withdraw,
    Vendor,
    Market,
    Gather,
    Craft,
    Entrust,
}

/// <summary>The words the JSON uses for the enums above (lower case, as types.ts has them).</summary>
public static class RunWords
{
    public static string Of(RunStatus s) => s.ToString().ToLowerInvariant();

    public static string Of(StepState s) => s.ToString().ToLowerInvariant();

    public static string Of(RunCommand c) => c.ToString().ToLowerInvariant();

    public static string Of(RunStepKind k) => k.ToString().ToLowerInvariant();

    public static RunStatus? Status(string? s) => Parse<RunStatus>(s);

    public static StepState? State(string? s) => Parse<StepState>(s);

    public static RunCommand? Command(string? s) => Parse<RunCommand>(s);

    public static RunStepKind? Kind(string? s) => Parse<RunStepKind>(s);

    /// <summary>A word as the JSON writes it (lower case, exactly), or null when it isn't one.</summary>
    private static T? Parse<T>(string? s) where T : struct, Enum
    {
        if (string.IsNullOrEmpty(s)) return null;
        foreach (var v in Enum.GetValues<T>())
            if (string.Equals(v.ToString().ToLowerInvariant(), s, StringComparison.Ordinal))
                return v;
        return null;
    }

    /// <summary>A run that's over: nothing more will happen to it.</summary>
    public static bool Finished(RunStatus s) => s is RunStatus.Done or RunStatus.Stopped or RunStatus.Failed;
}

/// <summary>Something to make, and how many.</summary>
public sealed record Target(uint Item, int Amount, bool Hq);

/// <summary>An Eorzea-time window: it opens at each of <see cref="Spawns"/> (Eorzea hours, 0–23) for <see cref="Duration"/> Eorzea minutes.</summary>
public sealed record TimedWindow(IReadOnlyList<int> Spawns, int Duration);

/// <summary>
/// One line of a step: an item, how many the step itself gets (<see cref="Amount"/>, for showing),
/// and how many must be <b>in your bags</b> once it's done (<see cref="Need"/>: so a step resumed after
/// a stop picks up where it was). The rest is the step kind's own (null or 0 where it doesn't apply).
/// </summary>
public sealed record RunLine(uint Item, int Amount, int Need, bool Hq = false)
{
    /// <summary>Withdraw: the retainer's name.</summary>
    public string? Retainer { get; init; }

    /// <summary>Vendor: gil for one.</summary>
    public long Price { get; init; }

    /// <summary>Vendor: the NPC's name.</summary>
    public string? Npc { get; init; }

    /// <summary>Vendor and gather: the zone's name.</summary>
    public string? Zone { get; init; }

    /// <summary>Market: the most gil for one (before the board's tax).</summary>
    public long MaxPrice { get; init; }

    /// <summary>Market: the world it's cheapest on, or null for "any" (the world you're on).</summary>
    public string? World { get; init; }

    /// <summary>Gather: the job (MIN, BTN, FSH).</summary>
    public string? Job { get; init; }

    /// <summary>Gather: the node's window, when it's a timed node.</summary>
    public TimedWindow? Timed { get; init; }
}

/// <summary>
/// One step. Every kind has <see cref="Lines"/>; a craft step has one (its result, <c>line</c> in the
/// JSON), with <see cref="Recipe"/>, <see cref="Job"/> and <see cref="Crafts"/> (syntheses to start).
/// </summary>
public sealed record RunStep(string Id, RunStepKind Kind, IReadOnlyList<RunLine> Lines)
{
    public uint Recipe { get; init; }
    public string? Job { get; init; }
    public int Crafts { get; init; }

    /// <summary>A craft step's result (the first line of any step).</summary>
    public RunLine Line => Lines[0];
}

public sealed record RunEstimate(double SellFor, double Spend, double Tax, double Gathered, double Profit, double Minutes)
{
    public static RunEstimate None { get; } = new(0, 0, 0, 0, 0, 0);
}

/// <summary>A run as the site sends it (<c>RunFile</c> in types.ts).</summary>
public sealed record RunFile(
    string Id,
    string Name,
    string CreatedAt,
    string? Character,
    IReadOnlyList<Target> Targets,
    long MaxGil,
    IReadOnlyList<RunStep> Steps,
    RunEstimate Estimate)
{
    public const string Format = "market-ledger-run";
    public const int Version = 1;

    /// <summary>The run's id as a Guid (the site's uuid), or Guid.Empty when it isn't one.</summary>
    public Guid Guid => Guid.TryParse(Id, out var g) ? g : Guid.Empty;

    /// <summary>Reads and checks a run file's JSON. Throws <see cref="FormatException"/> saying what's wrong.</summary>
    public static RunFile Parse(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"The run isn't JSON ({ex.Message}).");
        }
        return From(node);
    }

    /// <summary>Reads and checks a run file already parsed (the site's answer holds it as an object).</summary>
    public static RunFile From(JsonNode? node)
    {
        if (node is not JsonObject o) throw new FormatException("The run isn't an object.");
        if (JsonRead.Str(o, "format") != Format) throw new FormatException($"Not a Market Ledger run (format isn't \"{Format}\").");
        var version = JsonRead.Int(o, "version");
        if (version != Version) throw new FormatException($"A run of version {version?.ToString(CultureInfo.InvariantCulture) ?? "?"}: this plugin reads version {Version}. Update the plugin.");
        var id = JsonRead.Str(o, "id");
        if (string.IsNullOrWhiteSpace(id)) throw new FormatException("The run has no id.");
        var name = JsonRead.Str(o, "name") ?? "A run";
        var createdAt = JsonRead.Str(o, "createdAt") ?? string.Empty;
        var character = JsonRead.Str(o, "character");
        var maxGil = JsonRead.Long(o, "maxGil") ?? throw new FormatException("The run has no maxGil.");
        if (maxGil < 0) throw new FormatException("The run's maxGil is below 0.");

        var targets = new List<Target>();
        if (o["targets"] is JsonArray ta)
            foreach (var t in ta)
                if (t is JsonObject to)
                    targets.Add(new Target(Item(to, "target"), Count(to, "amount", "target"), JsonRead.Bool(to, "hq")));

        if (o["steps"] is not JsonArray sa) throw new FormatException("The run has no steps.");
        var steps = new List<RunStep>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < sa.Count; i++)
        {
            var step = Step(sa[i], i);
            if (!ids.Add(step.Id)) throw new FormatException($"Two steps have the id \"{step.Id}\".");
            steps.Add(step);
        }

        var estimate = RunEstimate.None;
        if (o["estimate"] is JsonObject e)
            estimate = new RunEstimate(JsonRead.Num(e, "sellFor") ?? 0, JsonRead.Num(e, "spend") ?? 0, JsonRead.Num(e, "tax") ?? 0,
                JsonRead.Num(e, "gathered") ?? 0, JsonRead.Num(e, "profit") ?? 0, JsonRead.Num(e, "minutes") ?? 0);

        return new RunFile(id.Trim(), name, createdAt, character, targets, maxGil, steps, estimate);
    }

    private static RunStep Step(JsonNode? n, int index)
    {
        var where = $"step {index + 1}";
        if (n is not JsonObject s) throw new FormatException($"The run's {where} isn't an object.");
        var id = JsonRead.Str(s, "id");
        if (string.IsNullOrWhiteSpace(id)) throw new FormatException($"The run's {where} has no id.");
        var kindWord = JsonRead.Str(s, "kind");
        if (RunWords.Kind(kindWord) is not { } kind) throw new FormatException($"The run's {where} is of a kind this plugin doesn't know (\"{kindWord}\"). Update the plugin.");
        if (kind == RunStepKind.Craft)
        {
            var recipe = JsonRead.Long(s, "recipe") ?? 0;
            if (recipe <= 0 || recipe > ushort.MaxValue) throw new FormatException($"The run's {where} has no recipe Artisan can take ({recipe}).");
            var crafts = JsonRead.Int(s, "crafts") ?? -1;
            if (crafts < 0) throw new FormatException($"The run's {where} has no number of crafts.");
            if (s["line"] is not JsonObject lo) throw new FormatException($"The run's {where} has no line.");
            return new RunStep(id, kind, new[] { Line(lo, kind, where) })
            {
                Recipe = (uint)recipe,
                Job = JsonRead.Str(s, "job"),
                Crafts = crafts,
            };
        }
        if (s["lines"] is not JsonArray la) throw new FormatException($"The run's {where} has no lines.");
        var lines = new List<RunLine>();
        foreach (var l in la)
        {
            if (l is not JsonObject lo) throw new FormatException($"A line of the run's {where} isn't an object.");
            lines.Add(Line(lo, kind, where));
        }
        if (lines.Count == 0) throw new FormatException($"The run's {where} has no lines.");
        return new RunStep(id, kind, lines);
    }

    private static RunLine Line(JsonObject l, RunStepKind kind, string where)
    {
        var line = new RunLine(Item(l, where), Count(l, "amount", where), Count(l, "need", where), JsonRead.Bool(l, "hq"));
        switch (kind)
        {
            case RunStepKind.Withdraw:
                var retainer = JsonRead.Str(l, "retainer");
                if (string.IsNullOrWhiteSpace(retainer)) throw new FormatException($"A line of the run's {where} names no retainer.");
                return line with { Retainer = retainer };
            case RunStepKind.Vendor:
                var price = JsonRead.Long(l, "price") ?? -1;
                if (price < 0) throw new FormatException($"A line of the run's {where} has no price.");
                return line with { Price = price, Npc = JsonRead.Str(l, "npc"), Zone = JsonRead.Str(l, "zone") };
            case RunStepKind.Market:
                var max = JsonRead.Long(l, "maxPrice") ?? -1;
                if (max <= 0) throw new FormatException($"A line of the run's {where} has no maxPrice.");
                return line with { MaxPrice = max, World = JsonRead.Str(l, "world") };
            case RunStepKind.Gather:
                TimedWindow? timed = null;
                if (l["timed"] is JsonObject t && t["spawns"] is JsonArray sp)
                {
                    var spawns = sp.Select(x => x is JsonValue v && v.TryGetValue<double>(out var d) ? (int)Math.Round(d) : -1)
                        .Where(h => h is >= 0 and <= 23).ToList();
                    if (spawns.Count > 0) timed = new TimedWindow(spawns, Math.Max(1, JsonRead.Int(t, "duration") ?? 0));
                }
                return line with { Job = JsonRead.Str(l, "job"), Zone = JsonRead.Str(l, "zone"), Timed = timed };
            default:
                return line;
        }
    }

    private static uint Item(JsonObject o, string where)
    {
        var item = JsonRead.Long(o, "item") ?? 0;
        if (item <= 0 || item > uint.MaxValue) throw new FormatException($"A line of the run's {where} has no item.");
        return (uint)item;
    }

    private static int Count(JsonObject o, string key, string where)
    {
        var n = JsonRead.Long(o, key);
        if (n is not { } v || v < 0 || v > 1_000_000) throw new FormatException($"A line of the run's {where} has no {key} (or one out of range).");
        return (int)v;
    }

    /// <summary>The run file written back as the site writes it (camelCase JSON).</summary>
    public JsonObject ToJson()
    {
        var steps = new JsonArray();
        foreach (var s in Steps)
        {
            var so = new JsonObject { ["id"] = s.Id, ["kind"] = RunWords.Of(s.Kind) };
            if (s.Kind == RunStepKind.Craft)
            {
                so["recipe"] = s.Recipe;
                so["job"] = s.Job;
                so["crafts"] = s.Crafts;
                so["line"] = LineJson(s.Line, s.Kind);
            }
            else
            {
                so["lines"] = new JsonArray(s.Lines.Select(l => (JsonNode)LineJson(l, s.Kind)).ToArray());
            }
            steps.Add(so);
        }
        return new JsonObject
        {
            ["format"] = Format,
            ["version"] = Version,
            ["id"] = Id,
            ["name"] = Name,
            ["createdAt"] = CreatedAt,
            ["character"] = Character,
            ["targets"] = new JsonArray(Targets.Select(t => (JsonNode)new JsonObject { ["item"] = t.Item, ["amount"] = t.Amount, ["hq"] = t.Hq }).ToArray()),
            ["maxGil"] = MaxGil,
            ["steps"] = steps,
            ["estimate"] = new JsonObject
            {
                ["sellFor"] = Estimate.SellFor,
                ["spend"] = Estimate.Spend,
                ["tax"] = Estimate.Tax,
                ["gathered"] = Estimate.Gathered,
                ["profit"] = Estimate.Profit,
                ["minutes"] = Estimate.Minutes,
            },
        };
    }

    private static JsonObject LineJson(RunLine l, RunStepKind kind)
    {
        var o = new JsonObject { ["item"] = l.Item, ["amount"] = l.Amount, ["need"] = l.Need };
        if (l.Hq) o["hq"] = true;
        switch (kind)
        {
            case RunStepKind.Withdraw:
                o["retainer"] = l.Retainer;
                break;
            case RunStepKind.Vendor:
                o["price"] = l.Price;
                o["npc"] = l.Npc;
                o["zone"] = l.Zone;
                break;
            case RunStepKind.Market:
                o["maxPrice"] = l.MaxPrice;
                o["world"] = l.World;
                break;
            case RunStepKind.Gather:
                o["job"] = l.Job;
                o["zone"] = l.Zone;
                if (l.Timed is { } t) o["timed"] = new JsonObject { ["spawns"] = new JsonArray(t.Spawns.Select(h => (JsonNode)JsonValue.Create(h)).ToArray()), ["duration"] = t.Duration };
                break;
        }
        return o;
    }
}

/// <summary>One step's progress, as <c>RunProgress.steps</c> has it.</summary>
public sealed record ProgressStep(string Id, StepState State, int Done, int Of, string? Note = null);

/// <summary>One line of a run's log, as <c>RunProgress.log</c> has it.</summary>
public sealed record ProgressLog(DateTime At, string Who, string Text);

/// <summary>
/// What the plugin says about a run as it goes (<c>RunProgress</c> in types.ts), sent with
/// <c>ml_plugin_run_update</c>. Times are UTC, written as ISO 8601.
/// </summary>
public sealed record RunProgress(
    RunStatus Status,
    int Step,
    IReadOnlyList<ProgressStep> Steps,
    long Spent,
    DateTime? StartedAt,
    DateTime UpdatedAt,
    IReadOnlyList<ProgressLog> Log,
    string? Message = null)
{
    /// <summary>The most log lines sent (the newest, newest first).</summary>
    public const int LogLines = 40;

    public static string Iso(DateTime t) => DateTime.SpecifyKind(t, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["status"] = RunWords.Of(Status),
            ["step"] = Step,
            ["steps"] = new JsonArray(Steps.Select(s =>
            {
                var so = new JsonObject { ["id"] = s.Id, ["state"] = RunWords.Of(s.State), ["done"] = s.Done, ["of"] = s.Of };
                if (s.Note != null) so["note"] = s.Note;
                return (JsonNode)so;
            }).ToArray()),
            ["spent"] = Spent,
            ["startedAt"] = StartedAt is { } st ? Iso(st) : null,
            ["updatedAt"] = Iso(UpdatedAt),
            ["log"] = new JsonArray(Log.Take(LogLines).Select(l => (JsonNode)new JsonObject { ["at"] = Iso(l.At), ["who"] = l.Who, ["text"] = l.Text }).ToArray()),
        };
        if (Message != null) o["message"] = Message;
        return o;
    }

    /// <summary>Reads progress as the site keeps it; null when it isn't readable.</summary>
    public static RunProgress? From(JsonNode? node)
    {
        if (node is not JsonObject o || RunWords.Status(JsonRead.Str(o, "status")) is not { } status) return null;
        var steps = new List<ProgressStep>();
        if (o["steps"] is JsonArray sa)
            foreach (var s in sa)
                if (s is JsonObject so && JsonRead.Str(so, "id") is { } id && RunWords.State(JsonRead.Str(so, "state")) is { } state)
                    steps.Add(new ProgressStep(id, state, JsonRead.Int(so, "done") ?? 0, JsonRead.Int(so, "of") ?? 0, JsonRead.Str(so, "note")));
        var log = new List<ProgressLog>();
        if (o["log"] is JsonArray la)
            foreach (var l in la)
                if (l is JsonObject lo && JsonRead.Time(lo, "at") is { } at)
                    log.Add(new ProgressLog(at, JsonRead.Str(lo, "who") ?? string.Empty, JsonRead.Str(lo, "text") ?? string.Empty));
        return new RunProgress(status, JsonRead.Int(o, "step") ?? 0, steps, JsonRead.Long(o, "spent") ?? 0, JsonRead.Time(o, "startedAt"),
            JsonRead.Time(o, "updatedAt") ?? DateTime.UtcNow, log, JsonRead.Str(o, "message"));
    }
}

/// <summary>Small readers for the site's JSON: a missing or wrongly typed value is null, never a throw.</summary>
public static class JsonRead
{
    public static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static bool Bool(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public static double? Num(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return double.IsFinite(d) ? d : null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        return null;
    }

    /// <summary>A whole number (a fraction is rounded, as the site's prices can be).</summary>
    public static long? Long(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d) && double.IsFinite(d) && Math.Abs(d) < 9e15) return (long)Math.Round(d);
        return null;
    }

    public static int? Int(JsonObject o, string key) => Long(o, key) is { } l && l is >= int.MinValue and <= int.MaxValue ? (int)l : null;

    public static DateTime? Time(JsonObject o, string key) =>
        Str(o, key) is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}
