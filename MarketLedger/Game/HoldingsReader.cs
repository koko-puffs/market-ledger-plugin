using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using MarketLedger.Automation;
using MarketLedger.Core;
using MarketLedger.Ipc;

namespace MarketLedger.Game;

/// <summary>
/// What the character you're playing holds, for the site (<c>ml_plugin_holdings</c>): your bags, the
/// crystals and your currencies (<see cref="ReadCurrencies"/>), read whenever asked; the saddlebags and each retainer as last seen open (a retainer's bags
/// whenever its inventory window is open, as Housing Studio's plugin counts them), kept per character in
/// holdings.json in the plugin's folder so they're still known after a restart; and Allagan Tools'
/// counts of your retainers when it's installed (the plugin's own count of a retainer wins). Framework
/// thread only (the file is written in the background).
/// </summary>
public sealed unsafe class HoldingsReader
{
    /// <summary>A retainer's seven bag pages and its crystals (what can be withdrawn at a bell).</summary>
    private static readonly InventoryType[] RetainerPages =
    {
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4,
        InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7, InventoryType.RetainerCrystals,
    };

    private static readonly InventoryType[] SaddleBags =
    {
        InventoryType.SaddleBag1, InventoryType.SaddleBag2, InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
    };

    /// <summary>The most retainers the site keeps for a character.</summary>
    public const int MaxRetainers = 12;

    /// <summary>The saddlebags' place name (a withdraw line naming it is taken by hand).</summary>
    public const string SaddlebagPlace = "Saddlebag";

    public sealed class Seen
    {
        public ulong Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public DateTime SeenAt { get; set; }
        public Dictionary<uint, int> Items { get; set; } = new();
    }

    public sealed class CharacterRecord
    {
        public List<Seen> Retainers { get; set; } = new();
        public Seen? Saddlebag { get; set; }
    }

    private readonly string path;
    private readonly object writing = new();
    private Dictionary<string, CharacterRecord> record = new();
    private DateTime nextLook;
    private long saveNumber;
    private long writtenNumber;

    public HoldingsReader()
    {
        path = Path.Combine(Services.PluginInterface.GetPluginConfigDirectory(), "holdings.json");
        try
        {
            if (File.Exists(path)) record = JsonSerializer.Deserialize<Dictionary<string, CharacterRecord>>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "Market Ledger couldn't read holdings.json: retainers are counted again as you open them");
            record = new();
        }
    }

    /// <summary>Goes up whenever what's seen of a retainer or the saddlebags changes.</summary>
    public int Version { get; private set; }

    private static string Me => Services.PlayerState.IsLoaded ? Services.PlayerState.ContentId.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>Looks (once a second) for an open retainer's bags and loaded saddlebags, and counts them.</summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;
        if (now < nextLook) return;
        nextLook = now + TimeSpan.FromSeconds(1);
        var me = Me;
        if (me.Length == 0 || me == "0") return;
        var mgr = InventoryManager.Instance();
        if (mgr == null) return;
        if (!record.TryGetValue(me, out var mine)) record[me] = mine = new CharacterRecord();

        // The saddlebags, while the game has them (it sends them when you open them).
        if (Read(mgr, SaddleBags) is { } bag && Changed(mine.Saddlebag, SaddlebagPlace, bag))
        {
            mine.Saddlebag = new Seen { Name = SaddlebagPlace, SeenAt = now, Items = bag };
            Version++;
            Save();
        }

        if (!ErrandTable.RetainerInventory.Any(GameUi.IsOpen)) return;
        var rm = RetainerManager.Instance();
        if (rm == null || !rm->IsReady) return;
        var active = rm->GetActiveRetainer();
        if (active == null || active->RetainerId == 0) return;
        if (Read(mgr, RetainerPages) is not { } items) return;
        var saved = mine.Retainers.FirstOrDefault(s => s.Id == active->RetainerId);
        var name = active->NameString;
        if (!Changed(saved, name, items)) return;
        if (saved == null) mine.Retainers.Add(saved = new Seen { Id = active->RetainerId });
        saved.Name = name;
        saved.SeenAt = now;
        saved.Items = items;
        Version++;
        Save();
    }

    private static bool Changed(Seen? saved, string name, Dictionary<uint, int> items) =>
        saved == null || saved.Name != name || saved.Items.Count != items.Count || saved.Items.Any(kv => !items.TryGetValue(kv.Key, out var n) || n != kv.Value);

    /// <summary>The counts in these containers, or null when the game hasn't sent any of them.</summary>
    private static Dictionary<uint, int>? Read(InventoryManager* mgr, InventoryType[] types)
    {
        var slots = new List<(uint, long)>();
        var loaded = false;
        foreach (var t in types)
        {
            var c = mgr->GetInventoryContainer(t);
            if (c == null || !c->IsLoaded) continue;
            loaded = true;
            for (var i = 0; i < c->Size; i++)
            {
                var item = c->GetInventorySlot(i);
                if (item == null || item->IsEmpty()) continue;
                slots.Add((item->GetBaseItemId(), item->GetQuantity()));
            }
        }
        return loaded ? Holdings.Count(slots) : null;
    }

    /// <summary>The tomestones' item ids (the game's <c>TomestonesItem</c> sheet), looked up once.</summary>
    private uint[]? tomestones;

    private uint[] Tomestones()
    {
        if (tomestones != null) return tomestones;
        try
        {
            tomestones = Services.Data.GetExcelSheet<TomestonesItem>().Select(r => r.Item.RowId).Where(id => id != 0).Distinct().ToArray();
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "Market Ledger couldn't read the game's list of tomestones: they're counted from the Currency container alone");
            tomestones = Array.Empty<uint>();
        }
        return tomestones;
    }

    /// <summary>
    /// Your currencies' balances (<see cref="Currencies"/>: gil left out, never added to the totals), or null
    /// while the game hasn't sent its Currency container (just after logging in). Read from:
    /// <list type="bullet">
    /// <item>the Currency container (<c>InventoryType.Currency</c>, 2000), every slot: gil, the Grand Company
    /// seals, Wolf Marks, Allied Seals, MGP and the Allagan tomestones live there;</item>
    /// <item><c>InventoryManager</c>'s own counters: <c>GetCompanySeals(1–3)</c> (Storm, Serpent, Flame Seals),
    /// <c>GetGoldSaucerCoin</c> (MGP), <c>GetWolfMarks</c>, <c>GetAlliedSeals</c>, and <c>GetTomestoneCount</c>
    /// for each tomestone in the game's <c>TomestonesItem</c> sheet;</item>
    /// <item><c>CurrencyManager</c>'s <c>SpecialItemBucket</c> (the scrips, Centurio Seals) and <c>ItemBucket</c>
    /// (Sacks of Nuts, Bicolor Gemstones, Skybuilders' Scrips, Trophy Crystals, the allied societies'
    /// currencies, ventures…), every item in them by <c>GetItemCount</c>, so a new currency appears without
    /// a change here (its <c>ContentItemBucket</c>, Island Sanctuary's and Bozja's, held only while there, isn't);</item>
    /// <item>and <see cref="Currencies.Named"/> one by one (<c>CurrencyManager.GetItemCount</c>, or
    /// <c>InventoryManager.GetInventoryItemCount</c> if it doesn't have it).</item>
    /// </list>
    /// A currency read twice is counted once (<see cref="Currencies.Place"/>).
    /// </summary>
    private Place? ReadCurrencies(InventoryManager* mgr)
    {
        var container = mgr->GetInventoryContainer(InventoryType.Currency);
        if (container == null || !container->IsLoaded) return null;
        var read = new List<(uint, long)>();
        for (var i = 0; i < container->Size; i++)
        {
            var item = container->GetInventorySlot(i);
            if (item == null || item->IsEmpty()) continue;
            read.Add((item->GetBaseItemId(), item->GetQuantity()));
        }
        for (byte gc = 1; gc <= 3; gc++) read.Add((Currencies.SealsOf(gc), mgr->GetCompanySeals(gc)));
        read.Add((Currencies.Mgp, mgr->GetGoldSaucerCoin()));
        read.Add((Currencies.WolfMarks, mgr->GetWolfMarks()));
        read.Add((Currencies.AlliedSeals, mgr->GetAlliedSeals()));
        foreach (var id in Tomestones()) read.Add((id, mgr->GetTomestoneCount(id)));
        var cm = CurrencyManager.Instance();
        if (cm != null)
        {
            var ids = new List<uint>();
            foreach (var id in cm->SpecialItemBucket.Keys) ids.Add(id);
            foreach (var id in cm->ItemBucket.Keys) ids.Add(id);
            foreach (var id in ids)
                if (id != 0) read.Add((id, cm->GetItemCount(id)));
        }
        foreach (var id in Currencies.Named)
            read.Add((id, cm != null && cm->HasItem(id) ? cm->GetItemCount(id) : mgr->GetInventoryItemCount(id)));
        return Currencies.Place(read);
    }

    /// <summary>
    /// Everything held, place by place: your bags, the crystals, your currencies, the saddlebags (as last seen), each
    /// retainer (as last seen, or Allagan Tools' count of one not seen; named from the game's list or
    /// AutoRetainer's). Empty when you aren't logged in.
    /// </summary>
    public List<Place> Places(AllaganTools? allagan, AutoRetainer? autoRetainer)
    {
        var places = new List<Place>();
        var me = Me;
        var mgr = InventoryManager.Instance();
        if (me.Length == 0 || me == "0" || mgr == null) return places;
        if (Read(mgr, InGame.Bags) is { } bags) places.Add(new Place("Bags", "bags", bags));
        if (Read(mgr, new[] { InventoryType.Crystals }) is { } crystals) places.Add(new Place("Crystals", "crystals", crystals));
        if (ReadCurrencies(mgr) is { } currencies) places.Add(currencies);
        record.TryGetValue(me, out var mine);
        if (mine?.Saddlebag is { } sb) places.Add(new Place(SaddlebagPlace, "saddlebag", sb.Items));
        var seen = new HashSet<ulong>();
        // The site keeps 12 retainers a character at most: the ones seen most lately.
        foreach (var r in (mine?.Retainers ?? new List<Seen>()).OrderByDescending(r => r.SeenAt).Take(MaxRetainers))
        {
            seen.Add(r.Id);
            places.Add(new Place(r.Name, "retainer", r.Items));
        }
        if (allagan != null && allagan.Installed)
        {
            var names = InGame.RetainerNames();
            if (names.Count == 0 && autoRetainer != null && autoRetainer.Installed) names = autoRetainer.RetainerNames(Services.PlayerState.ContentId);
            foreach (var (id, items) in allagan.Retainers())
                // Only retainers it can name: Allagan Tools' "characters owned" take in a company chest too.
                if (!seen.Contains(id) && seen.Count < MaxRetainers && names.TryGetValue(id, out var name) && seen.Add(id))
                    places.Add(new Place(name, "retainer", items));
        }
        return places;
    }

    private void Save()
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(record);
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "Market Ledger couldn't save what your retainers hold");
            return;
        }
        // Numbered: two saves close together can run in either order, and the older mustn't land last.
        var number = Interlocked.Increment(ref saveNumber);
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                lock (writing)
                {
                    if (number <= writtenNumber) return;
                    var temp = path + ".tmp";
                    File.WriteAllText(temp, json);
                    File.Move(temp, path, true);
                    writtenNumber = number;
                }
            }
            catch (Exception ex)
            {
                Services.Log.Warning(ex, "Market Ledger couldn't save what your retainers hold");
            }
        });
    }
}
