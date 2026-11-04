using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel.Sheets;

namespace MarketLedger.Game;

/// <summary>An NPC selling an item for gil, and where they stand: what a vendor step needs to walk there and buy it.</summary>
/// <param name="Price">Gil for one (the item's PriceMid, what a gil shop asks).</param>
/// <param name="ShopName">The shop's name as the NPC's menu lists it, or empty.</param>
/// <param name="District">A residential district's key ("mist") when the NPC stands in one, else null.</param>
public sealed record VendorOffer(uint ItemId, uint Price, uint NpcId, string NpcName, uint ShopId, string ShopName, uint TerritoryId, string PlaceName, Vector3? Position, string? District)
{
    public bool Located => TerritoryId != 0 && Position != null;

    public string Where() => $"{NpcName}, {(PlaceName.Length > 0 ? PlaceName : "somewhere not known")}";
}

/// <summary>The residential districts' outdoor areas (Housing Studio's plugin's Residential).</summary>
public static class Residential
{
    /// <summary>The district's key for a ward's territory name ("s1h1" Mist … "r1h1" Empyreum), or null.</summary>
    public static string? DistrictOf(string territoryName)
    {
        var n = territoryName.ToLowerInvariant();
        if (n.Length != 4 || n[1] != '1' || n[2] != 'h' || n[3] != '1') return null;
        return n[0] switch
        {
            's' => "mist",
            'f' => "lavender-beds",
            'w' => "goblet",
            'e' => "shirogane",
            'r' => "empyreum",
            _ => null,
        };
    }

    /// <summary>The aetheryte of the city whose Residential District Aethernet goes to a district (Lifestream's ResidentialAetheryteKind).</summary>
    public static uint CityAetheryte(string? district) => district switch
    {
        "mist" => 8,
        "lavender-beds" => 2,
        "goblet" => 9,
        "shirogane" => 111,
        "empyreum" => 70,
        _ => 0,
    };

    /// <summary>What an ENpcBase's ENpcData row is, by its range (the id's high 16 bits).</summary>
    public static int KindOf(uint dataId) => (int)(dataId >> 16);

    public const int GilShop = 0x0004;
    public const int CustomTalk = 0x000B;
    public const int TopicSelect = 0x0032;
    public const int PreHandler = 0x0036;
}

/// <summary>
/// Which NPCs sell which items for gil, and where they stand: worked out once from the game's data,
/// in the background, when the plugin loads (it goes through every NPC and every gil shop), then kept.
/// Until it's ready every item has no offers. As Housing Studio's plugin's VendorData: an NPC's shops
/// are found from its ENpcBase's ENpcData (a GilShop itself, or one reached through a TopicSelect, a
/// PreHandler or a CustomTalk's script arguments); its spot is its Level row, or for the residential
/// districts' NPCs (which have none) the wards' own layout files.
/// </summary>
public sealed class Vendors
{
    private volatile Dictionary<uint, List<VendorOffer>>? byItem;

    private volatile Dictionary<(string Name, uint Territory), List<Vector3>>? spotsByName;

    public bool Ready => byItem != null;

    public void WarmUp() => System.Threading.Tasks.Task.Run(() =>
    {
        try
        {
            var started = DateTime.UtcNow;
            Build();
            Services.Log.Debug($"Market Ledger read the vendors: {byItem?.Count ?? 0} items in {(DateTime.UtcNow - started).TotalMilliseconds:0} ms");
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "Market Ledger couldn't read the game's vendors: vendor steps can't find them");
            byItem = new Dictionary<uint, List<VendorOffer>>();
            spotsByName = new Dictionary<(string, uint), List<Vector3>>();
        }
    });

    /// <summary>Every NPC selling an item for gil (each place they stand), or none.</summary>
    public IReadOnlyList<VendorOffer> Offers(uint itemId) =>
        byItem is { } t && t.TryGetValue(itemId, out var list) ? list : (IReadOnlyList<VendorOffer>)Array.Empty<VendorOffer>();

    /// <summary>Everywhere an NPC of that name stands in a territory (a ward's main division and its subdivision each have one).</summary>
    public IReadOnlyList<Vector3> SpotsOf(string npcName, uint territory) =>
        spotsByName is { } s && s.TryGetValue((npcName, territory), out var list) ? list : (IReadOnlyList<Vector3>)Array.Empty<Vector3>();

    /// <summary>
    /// The offer a vendor line means: the NPC and zone the site named when one matches, else (by the
    /// game's data) one standing somewhere known outside the residential districts, then one in them;
    /// the cheapest of those. <paramref name="usable"/> rules out what can't be reached (an aetheryte not attuned).
    /// </summary>
    public VendorOffer? Choose(uint itemId, string? npc, string? zone, Func<VendorOffer, bool> usable)
    {
        var offers = Offers(itemId).Where(o => o.Located && usable(o)).ToList();
        if (offers.Count == 0) return null;
        bool Same(string a, string? b) => b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        return offers.FirstOrDefault(o => Same(o.NpcName, npc) && Same(o.PlaceName, zone))
               ?? offers.Where(o => Same(o.NpcName, npc)).OrderBy(o => o.District != null).ThenBy(o => o.Price).FirstOrDefault()
               ?? offers.OrderBy(o => o.District != null).ThenBy(o => o.Price).ThenBy(o => o.NpcId).FirstOrDefault();
    }

    private readonly record struct Spot(uint Territory, Vector3 Position);

    private void Build()
    {
        var items = Services.Data.GetExcelSheet<Item>();
        var shopItems = new Dictionary<uint, List<uint>>();
        foreach (var row in Services.Data.GetSubrowExcelSheet<GilShopItem>())
            foreach (var sub in row)
            {
                var id = sub.Item.RowId;
                if (id == 0) continue;
                // Behind a quest or an achievement: left out (the shop may not show it).
                if (sub.AchievementRequired.RowId != 0) continue;
                var quest = false;
                foreach (var q in sub.QuestRequired)
                    quest |= q.RowId != 0;
                if (quest) continue;
                if (!shopItems.TryGetValue(row.RowId, out var list)) shopItems[row.RowId] = list = new List<uint>();
                list.Add(id);
            }

        var shopNames = new Dictionary<uint, string>();
        var gilShops = Services.Data.GetExcelSheet<GilShop>();
        foreach (var id in shopItems.Keys)
            shopNames[id] = gilShops.TryGetRow(id, out var shop) ? shop.Name.ExtractText() : string.Empty;

        var topics = Services.Data.GetExcelSheet<TopicSelect>();
        var handlers = Services.Data.GetExcelSheet<PreHandler>();
        var talks = Services.Data.GetExcelSheet<CustomTalk>();
        var reached = new Dictionary<uint, uint[]>();
        void ShopsOf(uint dataId, HashSet<uint> into, int depth)
        {
            if (dataId == 0 || depth > 3) return;
            var kind = Residential.KindOf(dataId);
            if (kind == Residential.GilShop)
            {
                if (shopItems.ContainsKey(dataId)) into.Add(dataId);
                return;
            }
            if (kind is not (Residential.TopicSelect or Residential.PreHandler or Residential.CustomTalk)) return;
            if (reached.TryGetValue(dataId, out var known))
            {
                into.UnionWith(known);
                return;
            }
            var found = new HashSet<uint>();
            switch (kind)
            {
                case Residential.TopicSelect:
                    if (topics.TryGetRow(dataId, out var topic))
                        foreach (var s in topic.Shop)
                            ShopsOf(s.RowId, found, depth + 1);
                    break;
                case Residential.PreHandler:
                    if (handlers.TryGetRow(dataId, out var pre)) ShopsOf(pre.Target.RowId, found, depth + 1);
                    break;
                default:
                    if (talks.TryGetRow(dataId, out var talk))
                        foreach (var s in talk.Script)
                            if (Residential.KindOf(s.ScriptArg) == Residential.GilShop) ShopsOf(s.ScriptArg, found, depth + 1);
                    break;
            }
            reached[dataId] = found.ToArray();
            into.UnionWith(found);
        }
        var npcShops = new Dictionary<uint, HashSet<uint>>();
        var scratch = new HashSet<uint>();
        foreach (var npc in Services.Data.GetExcelSheet<ENpcBase>())
        {
            scratch.Clear();
            foreach (var d in npc.ENpcData)
                ShopsOf(d.RowId, scratch, 0);
            if (scratch.Count > 0) npcShops[npc.RowId] = new HashSet<uint>(scratch);
        }

        var spots = new Dictionary<uint, List<Spot>>();
        void AddSpot(uint npc, Spot spot)
        {
            if (!spots.TryGetValue(npc, out var list)) spots[npc] = list = new List<Spot>();
            if (list.All(s => s.Territory != spot.Territory || Vector3.Distance(s.Position, spot.Position) > 3)) list.Add(spot);
        }
        foreach (var level in Services.Data.GetExcelSheet<Level>())
        {
            var npc = level.Object.RowId;
            if (npc == 0 || !npcShops.ContainsKey(npc) || level.Territory.RowId == 0) continue;
            AddSpot(npc, new Spot(level.Territory.RowId, new Vector3(level.X, level.Y, level.Z)));
        }
        var territories = Services.Data.GetExcelSheet<TerritoryType>();
        var layoutsRead = new HashSet<string>();
        foreach (var t in territories)
        {
            if (Residential.DistrictOf(t.Name.ExtractText()) == null) continue;
            var bg = t.Bg.ExtractText();
            var cut = bg.IndexOf("/level/", StringComparison.Ordinal);
            if (cut < 0 || !layoutsRead.Add(bg[..cut])) continue;
            foreach (var file in new[] { "planevent.lgb", "planlive.lgb", "bg.lgb" })
            {
                LgbFile? lgb = null;
                try
                {
                    lgb = Services.Data.GetFile<LgbFile>($"bg/{bg[..cut]}/level/{file}");
                }
                catch (Exception ex)
                {
                    Services.Log.Debug(ex, $"Couldn't read {file} of {t.Name.ExtractText()}");
                }
                if (lgb == null) continue;
                foreach (var layer in lgb.Layers)
                    foreach (var obj in layer.InstanceObjects)
                    {
                        if (obj.AssetType != LayerEntryType.EventNPC || obj.Object is not LayerCommon.ENPCInstanceObject e) continue;
                        var npc = e.ParentData.ParentData.BaseId;
                        if (!npcShops.ContainsKey(npc)) continue;
                        var p = obj.Transform.Translation;
                        AddSpot(npc, new Spot(t.RowId, new Vector3(p.X, p.Y, p.Z)));
                    }
            }
        }

        var residents = Services.Data.GetExcelSheet<ENpcResident>();
        var places = new Dictionary<uint, (string Place, string? District)>();
        (string Place, string? District) PlaceOf(uint territory)
        {
            if (places.TryGetValue(territory, out var p)) return p;
            p = territories.TryGetRow(territory, out var t)
                ? (t.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty, Residential.DistrictOf(t.Name.ExtractText()))
                : (string.Empty, null);
            places[territory] = p;
            return p;
        }
        var offers = new Dictionary<uint, List<VendorOffer>>();
        var names = new Dictionary<(string, uint), List<Vector3>>();
        foreach (var (npc, shops) in npcShops)
        {
            var name = residents.TryGetRow(npc, out var res) ? NpcName(res.Singular.ExtractText()) : string.Empty;
            if (name.Length == 0) name = "Vendor";
            var at = spots.TryGetValue(npc, out var list) ? list : new List<Spot>();
            foreach (var spot in at)
            {
                if (!names.TryGetValue((name, spot.Territory), out var where)) names[(name, spot.Territory)] = where = new List<Vector3>();
                where.Add(spot.Position);
            }
            foreach (var shop in shops)
                foreach (var item in shopItems[shop])
                {
                    var price = items.TryGetRow(item, out var it) ? it.PriceMid : 0;
                    if (price == 0) continue;
                    if (!offers.TryGetValue(item, out var l)) offers[item] = l = new List<VendorOffer>();
                    foreach (var spot in at)
                    {
                        var (place, district) = PlaceOf(spot.Territory);
                        l.Add(new VendorOffer(item, price, npc, name, shop, shopNames[shop], spot.Territory, place, spot.Position, district));
                    }
                }
        }
        spotsByName = names;
        byItem = offers;
    }

    /// <summary>An NPC's name as the game writes it in a sentence ("material supplier") with capitals, as on its nameplate.</summary>
    private static string NpcName(string singular) =>
        singular.Length > 0 && char.IsLower(singular[0]) ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(singular) : singular;
}
