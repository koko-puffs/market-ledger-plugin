using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace MarketLedger.Core;

/// <summary>One place things are held: your bags, the crystals, the saddlebags, a retainer, your currencies.</summary>
/// <param name="Kind">"bags", "crystals", "saddlebag", "retainer" or "currency", as types.ts has it.</param>
public sealed record Place(string Name, string Kind, IReadOnlyDictionary<uint, int> Items);

/// <summary>What a character holds, as the site takes it (<c>ml_plugin_holdings</c>). Pure, so it's tested.</summary>
public static class Holdings
{
    /// <summary>
    /// Every place's items together (HQ counted with NQ, as read): the materials. The currencies' place
    /// (<see cref="Currencies.Kind"/>) is left out: they aren't materials, and the site values them apart.
    /// </summary>
    public static Dictionary<uint, int> Total(IEnumerable<Place> places)
    {
        var total = new Dictionary<uint, int>();
        foreach (var p in places)
        {
            if (p.Kind == Currencies.Kind) continue;
            foreach (var (id, n) in p.Items)
                if (n > 0) total[id] = total.GetValueOrDefault(id) + n;
        }
        return total;
    }

    /// <summary><c>p_places</c>: each place, its kind and its items.</summary>
    public static JsonArray PlacesJson(IEnumerable<Place> places) =>
        new(places.Select(p => (JsonNode)new JsonObject
        {
            ["place"] = p.Name,
            ["kind"] = p.Kind,
            ["items"] = SiteLink.Counts(p.Items),
        }).ToArray());

    /// <summary>A short fingerprint of what's held, to tell whether anything changed since the last send.</summary>
    public static string Fingerprint(IEnumerable<Place> places)
    {
        var sb = new StringBuilder();
        foreach (var p in places.OrderBy(p => p.Kind, StringComparer.Ordinal).ThenBy(p => p.Name, StringComparer.Ordinal))
        {
            sb.Append(p.Kind).Append(':').Append(p.Name).Append('|');
            foreach (var (id, n) in p.Items.Where(kv => kv.Value > 0).OrderBy(kv => kv.Key)) sb.Append(id).Append('=').Append(n).Append(',');
            sb.Append(';');
        }
        return sb.ToString();
    }

    /// <summary>Counts of (item id, quantity) pairs, HQ and collectable copies counted as the item.</summary>
    public static Dictionary<uint, int> Count(IEnumerable<(uint Item, long Quantity)> slots)
    {
        var counts = new Dictionary<uint, int>();
        foreach (var (item, q) in slots)
        {
            if (item == 0 || q <= 0) continue;
            // HQ ids are the item's + 1,000,000, collectables + 500,000 (Allagan Tools' numbers keep them).
            var id = item >= 1_000_000 ? item - 1_000_000 : item >= 500_000 ? item - 500_000 : item;
            counts[id] = (int)Math.Min(int.MaxValue, counts.GetValueOrDefault(id) + q);
        }
        return counts;
    }

    /// <summary>
    /// A retainer's counts from Allagan Tools' <c>GetCharacterItems</c>: each slot is CriticalCommonLib's
    /// <c>InventoryItem.ToNumeric()</c>, [0] its container (CriticalCommonLib's InventoryType), [1] its slot,
    /// [2] the item id, [3] the quantity. Only what can be withdrawn at a bell counts: the retainer's seven
    /// bag pages (10000–10006) and its crystals (12001), not what it's selling (12002), wearing (11000) or
    /// its gil (12000).
    /// </summary>
    public static Dictionary<uint, int> FromAllagan(IEnumerable<ulong[]> slots) =>
        Count(slots.Where(a => a is { Length: >= 4 } && a[0] is (>= 10000 and <= 10006) or 12001)
            .Select(a => ((uint)Math.Min(a[2], uint.MaxValue), (long)Math.Min(a[3], (ulong)long.MaxValue))));

    /// <summary>
    /// Retainers' names by id from AutoRetainer's <c>GetOfflineCharacterData</c>, as Dalamud hands it to a
    /// plugin that can't name its type: written out as JSON (its <c>RetainerData</c> [{ <c>Name</c>,
    /// <c>RetainerID</c> }]). Anything unreadable is left out.
    /// </summary>
    public static Dictionary<ulong, string> RetainerNamesFromJson(string? json)
    {
        var names = new Dictionary<ulong, string>();
        if (string.IsNullOrWhiteSpace(json)) return names;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return names;
        }
        if (node is not JsonObject o || o["RetainerData"] is not JsonArray list) return names;
        foreach (var r in list)
        {
            if (r is not JsonObject ro || ro["RetainerID"] is not JsonValue idv || JsonRead.Str(ro, "Name") is not { Length: > 0 } name) continue;
            ulong id = idv.TryGetValue<ulong>(out var u) ? u : idv.TryGetValue<long>(out var l) && l > 0 ? (ulong)l : 0;
            if (id != 0) names[id] = name;
        }
        return names;
    }
}

/// <summary>
/// The character's currencies, as one place for the site (<c>{ place: "Currencies", kind: "currency",
/// items: { currency item id: amount } }</c>): scrips, tomestones, Grand Company seals, MGP, Wolf Marks,
/// hunt seals, Bicolor Gemstones, allied societies' currencies and the like, read by
/// <c>Game/HoldingsReader.cs</c> from the game's Currency container, its <c>CurrencyManager</c> and
/// <c>InventoryManager</c>'s own counters. Never added to the totals (<see cref="Holdings.Total"/>). Pure, so it's tested.
/// </summary>
public static class Currencies
{
    /// <summary>The place's kind, as types.ts and <c>ml_plugin_holdings</c> have it.</summary>
    public const string Kind = "currency";

    /// <summary>The place's name.</summary>
    public const string PlaceName = "Currencies";

    /// <summary>Gil: said in <c>ml_plugin_hello</c>, never a currency here.</summary>
    public const uint Gil = 1;

    /// <summary>The most of one currency the site keeps (as for any item count).</summary>
    public const int MaxAmount = 100_000_000;

    /// <summary>The most currencies the site keeps (the lowest ids first).</summary>
    public const int MaxCurrencies = 200;

    public const uint StormSeals = 20;
    public const uint SerpentSeals = 21;
    public const uint FlameSeals = 22;
    public const uint WolfMarks = 25;
    public const uint AlliedSeals = 27;
    public const uint Mgp = 29;
    public const uint CenturioSeals = 10307;
    public const uint SacksOfNuts = 26533;
    public const uint BicolorGemstones = 26807;
    public const uint TrophyCrystals = 36656;

    /// <summary>A Grand Company's seals (the game's company ids: 1 the Maelstrom, 2 the Order of the Twin Adder, 3 the Immortal Flames), or 0.</summary>
    public static uint SealsOf(byte grandCompany) => grandCompany switch
    {
        1 => StormSeals,
        2 => SerpentSeals,
        3 => FlameSeals,
        _ => 0,
    };

    /// <summary>Currencies read one by one besides those found by looking through the game's lists (in case a list misses one).</summary>
    public static readonly uint[] Named = { CenturioSeals, SacksOfNuts, BicolorGemstones, TrophyCrystals };

    /// <summary>
    /// The place from what was read, (currency item id, amount) from wherever: gil, id 0 and nothing
    /// held left out; one currency read in two places (the Currency container and a counter, say)
    /// counted once, the larger (it's the same balance, read twice); each at most
    /// <see cref="MaxAmount"/>; at most <see cref="MaxCurrencies"/>, the lowest ids first.
    /// </summary>
    public static Place Place(IEnumerable<(uint Item, long Amount)> readings)
    {
        var amounts = new Dictionary<uint, int>();
        foreach (var (item, amount) in readings)
        {
            if (item == 0 || item == Gil || amount <= 0) continue;
            var n = (int)Math.Min(MaxAmount, amount);
            if (n > amounts.GetValueOrDefault(item)) amounts[item] = n;
        }
        var kept = amounts.Count <= MaxCurrencies
            ? amounts
            : amounts.OrderBy(kv => kv.Key).Take(MaxCurrencies).ToDictionary(kv => kv.Key, kv => kv.Value);
        return new Place(PlaceName, Kind, kept);
    }
}

/// <summary>A sale a retainer made, as the site takes it (<c>ml_plugin_sales</c>).</summary>
public sealed record SaleRow(string Retainer, uint Item, bool Hq, int Quantity, long Price, DateTime At)
{
    /// <summary>The same sale read twice gives the same key (the site keeps it once too).</summary>
    public string Key => $"{Retainer}|{Item}|{(Hq ? 1 : 0)}|{Quantity}|{Price}|{At.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}";

    public JsonObject ToJson() => new()
    {
        ["retainer"] = Retainer,
        ["item"] = Item,
        ["hq"] = Hq,
        ["quantity"] = Quantity,
        ["price"] = Price,
        ["at"] = RunProgress.Iso(At),
    };
}

/// <summary>What a retainer has on the market (<c>ml_plugin_listings</c>).</summary>
public sealed record ListingRow(uint Item, bool Hq, int Quantity, long Price)
{
    public JsonObject ToJson() => new() { ["item"] = Item, ["hq"] = Hq, ["quantity"] = Quantity, ["price"] = Price };

    public static string Fingerprint(string retainer, IEnumerable<ListingRow> rows) =>
        retainer + "|" + string.Join(",", rows.OrderBy(r => r.Item).ThenBy(r => r.Price).Select(r => $"{r.Item}:{(r.Hq ? 1 : 0)}:{r.Quantity}:{r.Price}"));
}

/// <summary>A value of a game window (its AtkValues), as the reader hands it over: a number or a text.</summary>
public readonly record struct UiValue(long? Number, string? Text)
{
    public static UiValue Of(long n) => new(n, null);

    public static UiValue Of(string s) => new(null, s);
}

/// <summary>
/// Reading a retainer's sale history window (the retainer's "View sale history", the game's
/// <c>RetainerHistory</c> window). Neither FFXIVClientStructs nor Dalamud describe it, so its rows are
/// found in the window's values by their shape: an item's name (with the game's HQ mark when it's high
/// quality), then, within the next few values, a price, a quantity and a time (seconds since 1970). A
/// row without all four isn't kept. Not yet checked against the game: when nothing reads, the
/// plugin logs the window's values once so it can be fixed. Pure, so it's tested.
/// </summary>
public static class SaleHistory
{
    /// <summary>The game's HQ mark after an item's name.</summary>
    public const char HqMark = '';

    /// <summary>How far after a name its numbers are looked for.</summary>
    public const int Window = 8;

    private const long EarliestTime = 1_500_000_000;
    private const long LatestTime = 4_000_000_000;

    public static List<SaleRow> Parse(string retainer, IReadOnlyList<UiValue> values, Func<string, uint> itemByName, DateTime now)
    {
        var rows = new List<SaleRow>();
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i].Text is not { Length: > 0 } text) continue;
            var hq = text.Contains(HqMark);
            var name = text.Replace(HqMark.ToString(), string.Empty, StringComparison.Ordinal).Trim();
            if (name.Length == 0) continue;
            var item = itemByName(name);
            if (item == 0) continue;
            long? price = null, quantity = null, time = null;
            var end = Math.Min(values.Count, i + 1 + Window);
            for (var k = i + 1; k < end; k++)
            {
                if (values[k].Text is { Length: > 0 } t && itemByName(t.Replace(HqMark.ToString(), string.Empty, StringComparison.Ordinal).Trim()) != 0) break;
                if (values[k].Number is not { } n || n <= 0) continue;
                if (n is >= EarliestTime and <= LatestTime) time ??= n;
                else if (price == null) price = n;
                else if (quantity == null && n <= 9999) quantity = n;
            }
            if (price == null || quantity == null || time == null) continue;
            // The price may come as the whole sale's: the price for one is what the site keeps.
            var at = DateTimeOffset.FromUnixTimeSeconds(time.Value).UtcDateTime;
            if (at > now.AddDays(1)) continue;
            var unit = price.Value;
            rows.Add(new SaleRow(retainer, item, hq, (int)quantity.Value, unit, at));
        }
        return rows;
    }
}

/// <summary>The game's jobs that make or gather things, by abbreviation (as the site's <c>JobLevels</c>).</summary>
public static class Jobs
{
    public static readonly string[] Crafters = { "CRP", "BSM", "ARM", "GSM", "LTW", "WVR", "ALC", "CUL" };
    public static readonly string[] Gatherers = { "MIN", "BTN", "FSH" };

    public static bool Wanted(string abbreviation) => Array.IndexOf(Crafters, abbreviation) >= 0 || Array.IndexOf(Gatherers, abbreviation) >= 0;

    public static JsonObject ToJson(IReadOnlyDictionary<string, int> levels)
    {
        var o = new JsonObject();
        foreach (var (job, level) in levels.Where(kv => Wanted(kv.Key) && kv.Value > 0).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            o[job] = level;
        return o;
    }
}

/// <summary>
/// A gather list for GatherBuddy Reborn as text, when it can't be handed over through its IPC: one
/// "12x Item Name" line each, the TeamCraft format its Auto-Gather tab's "Import from TeamCraft" reads
/// (<c>GatherBuddy/Gui/Interface.AutoGatherTab.cs</c>: <c>\b(\d+)x\s(.+)\b</c>).
/// </summary>
public static class GatherList
{
    public static string Teamcraft(IEnumerable<(string Name, int Quantity)> items) =>
        string.Join("\n", items.Where(i => i.Quantity > 0 && i.Name.Length > 0).Select(i => $"{i.Quantity.ToString(CultureInfo.InvariantCulture)}x {i.Name}"));

    /// <summary>The list's name in GatherBuddy Reborn for a run: one per run, so a new run never touches your own lists.</summary>
    public static string Name(string runName) => $"Market Ledger · {(runName.Length > 40 ? runName[..40] : runName)}";
}
