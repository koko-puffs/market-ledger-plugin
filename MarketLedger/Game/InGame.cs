using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using MarketLedger.Automation;
using MarketLedger.Core;
using Lumina.Excel.Sheets;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace MarketLedger.Game;

/// <summary>A market city: the aetheryte's territory and name, the boards' territory, the aethernet shard between (or null), and the boards' place.</summary>
public readonly record struct MarketCity(uint AetheryteTerritory, string Name, uint BoardTerritory, string? Aethernet, string BoardPlace);

/// <summary>
/// What a run reads and does in the game beyond windows: your bags and gil, stack sizes, worlds,
/// aetherytes, the objects around you, talking to one, the retainers' bags, the board's listings, and
/// whether it's safe to carry on (Housing Studio's plugin's ErrandGame). Framework thread only.
/// </summary>
public static unsafe class InGame
{
    public static readonly InventoryType[] RetainerPages =
    {
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4,
        InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7,
    };

    /// <summary>Limsa Lominsa Lower Decks: its aetheryte stands above Hawkers' Alley, where the market boards and summoning bells are.</summary>
    public const uint HubTerritory = 129;

    /// <summary>Ul'dah - Steps of Nald: its aetheryte stands by the Sapphire Avenue Exchange's market boards and summoning bells.</summary>
    public const uint UldahTerritory = 130;

    /// <summary>Ul'dah - Steps of Thal: the Sapphire Avenue Exchange, its market boards and bells (an aethernet hop from the aetheryte).</summary>
    public const uint UldahBoardTerritory = 131;

    /// <summary>
    /// The city the errands use for the market, the bells and world visits (Settings → Getting → Market
    /// city): the aetheryte's territory, and where its boards are, with the aethernet shard between.
    /// Ul'dah's boards are at the Sapphire Avenue Exchange, in the Steps of Thal: 0.7.9 looked for them by
    /// the aetheryte (you took the aethernet there, in its recording).
    /// </summary>
    public static MarketCity Hub(int choice) => choice == 1
        ? new MarketCity(UldahTerritory, "Ul'dah - Steps of Nald", UldahBoardTerritory, "Sapphire Avenue Exchange", "the Sapphire Avenue Exchange")
        : new MarketCity(HubTerritory, "Limsa Lominsa Lower Decks", HubTerritory, null, "Limsa Lominsa Lower Decks");

    /// <summary>Where a market city's boards and bells are (Limsa's Hawkers' Alley, Ul'dah's Sapphire Avenue).</summary>
    public static bool AtABoardCity => Territory is HubTerritory or UldahBoardTerritory;

    /// <summary>At a city aetheryte a world visit leaves from (Limsa's, Ul'dah's).</summary>
    public static bool AtAWorldVisitAetheryte => Territory is HubTerritory or UldahTerritory;

    /// <summary>Free slots in your four bags.</summary>
    public static int FreeSlots()
    {
        var mgr = InventoryManager.Instance();
        return mgr == null ? 0 : (int)mgr->GetEmptySlotsInBag();
    }

    public static long Gil()
    {
        var mgr = InventoryManager.Instance();
        return mgr == null ? 0 : mgr->GetGil();
    }

    /// <summary>How many of an item your bags hold (HQ copies too).</summary>
    public static int BagCount(uint itemId)
    {
        var mgr = InventoryManager.Instance();
        if (mgr == null) return 0;
        return mgr->GetInventoryItemCount(itemId, false, false, false, 0) + mgr->GetInventoryItemCount(itemId, true, false, false, 0);
    }

    /// <summary>How many normal-quality copies of an item your bags hold: what a bought one stacks onto.</summary>
    public static int NqCount(uint itemId)
    {
        var mgr = InventoryManager.Instance();
        return mgr == null ? 0 : mgr->GetInventoryItemCount(itemId, false, false, false, 0);
    }

    private static readonly Dictionary<uint, int> Stacks = new();

    /// <summary>How many make a stack (the Item sheet's StackSize), 1 when not known.</summary>
    public static int StackSize(uint itemId)
    {
        if (Stacks.TryGetValue(itemId, out var n)) return n;
        n = Services.Data.GetExcelSheet<Item>().TryGetRow(itemId, out var item) ? (int)Math.Max(1, item.StackSize) : 1;
        Stacks[itemId] = n;
        return n;
    }

    private static readonly Dictionary<uint, string> Names = new();

    /// <summary>An item's name in your client's language (any item, not only furnishings).</summary>
    public static string ItemName(uint itemId)
    {
        if (Names.TryGetValue(itemId, out var n)) return n;
        n = Services.Data.GetExcelSheet<Item>().TryGetRow(itemId, out var item) ? item.Name.ExtractText() : string.Empty;
        Names[itemId] = n;
        return n;
    }

    public static string CurrentWorld() => Services.PlayerState.IsLoaded ? Services.PlayerState.CurrentWorld.ValueNullable?.Name.ExtractText() ?? string.Empty : string.Empty;

    /// <summary>The world you're on now (its World row), or 0.</summary>
    public static uint CurrentWorldId() => Services.PlayerState.IsLoaded ? Services.PlayerState.CurrentWorld.RowId : 0;

    public static string HomeWorld() => Services.PlayerState.IsLoaded ? Services.PlayerState.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty : string.Empty;

    private static Dictionary<uint, uint>? aetherytes;

    /// <summary>The main aetheryte standing in a territory (one you teleport to), or 0 when it has none.</summary>
    public static uint AetheryteOf(uint territory)
    {
        if (aetherytes == null)
        {
            aetherytes = new Dictionary<uint, uint>();
            foreach (var a in Services.Data.GetExcelSheet<Aetheryte>())
                if (a.IsAetheryte && a.Territory.RowId != 0 && !aetherytes.ContainsKey(a.Territory.RowId))
                    aetherytes[a.Territory.RowId] = a.RowId;
        }
        return aetherytes.TryGetValue(territory, out var id) ? id : 0;
    }

    /// <summary>Whether the aetheryte is one you've attuned to (else the game won't teleport there).</summary>
    public static bool Attuned(uint aetheryte)
    {
        var ui = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
        return ui != null && ui->IsAetheryteUnlocked(aetheryte);
    }

    public static uint Territory => Services.ClientState.TerritoryType;

    public static Vector3? Me => Services.Objects.LocalPlayer?.Position;

    public static float DistanceTo(Vector3 p) => Me is { } me ? Vector3.Distance(me, p) : float.MaxValue;

    /// <summary>Whether something in the game rules out carrying on, and what (null when nothing does).</summary>
    public static string? Blocked(bool travelling)
    {
        var c = Services.Condition;
        if (c[ConditionFlag.InCombat]) return "You're in combat.";
        if (c[ConditionFlag.BoundByDuty] || c[ConditionFlag.BoundByDuty56] || c[ConditionFlag.BoundByDuty95]) return "You're in a duty.";
        if (GameUi.IsOpen("ContentsFinderConfirm")) return "A duty popped.";
        if (c[ConditionFlag.WatchingCutscene] || c[ConditionFlag.WatchingCutscene78] || c[ConditionFlag.OccupiedInCutSceneEvent]) return "A cutscene started.";
        if (c[ConditionFlag.LoggingOut]) return "You're logging out.";
        if (c[ConditionFlag.Unconscious]) return "You were knocked out.";
        if (!travelling && (c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51])) return "You changed zones when no travel was asked for.";
        return null;
    }

    /// <summary>Between zones, teleporting, or casting (a teleport's cast).</summary>
    public static bool Moving => Services.Condition[ConditionFlag.BetweenAreas] || Services.Condition[ConditionFlag.BetweenAreas51]
                                 || Services.Condition[ConditionFlag.Casting] || Services.Condition[ConditionFlag.Jumping61]
                                 || Services.Condition[ConditionFlag.WaitingToVisitOtherWorld];

    /// <summary>Standing still somewhere: logged in, not between zones, the player there.</summary>
    public static bool Settled => Services.ClientState.IsLoggedIn && Services.Objects.LocalPlayer != null && !Moving;

    // ---------------------------------------------------------------------------------------------
    // Objects around you.

    private static readonly Dictionary<uint, string> ObjectNames = new();

    /// <summary>An event object's name in your client's language (EObjName), or empty.</summary>
    public static string EObjName(uint row)
    {
        if (ObjectNames.TryGetValue(row, out var n)) return n;
        n = Services.Data.GetExcelSheet<EObjName>().TryGetRow(row, out var r) ? r.Singular.ExtractText() : string.Empty;
        ObjectNames[row] = n;
        return n;
    }

    /// <summary>The nearest targetable object (of those kinds) whose name is one of <paramref name="names"/>, within reach of <paramref name="from"/>.</summary>
    public static IGameObject? Nearest(IReadOnlyCollection<string> names, Vector3 from, float within, params ObjectKind[] kinds)
    {
        IGameObject? best = null;
        var bestD = within;
        foreach (var o in Services.Objects)
        {
            if (o == null || !o.IsTargetable || (kinds.Length > 0 && Array.IndexOf(kinds, o.ObjectKind) < 0)) continue;
            var d = Vector3.Distance(o.Position, from);
            if (d > bestD) continue;
            var name = o.Name.TextValue;
            if (!names.Any(w => string.Equals(w, name, StringComparison.OrdinalIgnoreCase))) continue;
            best = o;
            bestD = d;
        }
        return best;
    }

    /// <summary>The nearest object (of those kinds) whose name has one of <paramref name="words"/> in it.</summary>
    public static IGameObject? NearestContaining(IReadOnlyCollection<string> words, Vector3 from, float within, params ObjectKind[] kinds)
    {
        IGameObject? best = null;
        var bestD = within;
        foreach (var o in Services.Objects)
        {
            if (o == null || !o.IsTargetable || (kinds.Length > 0 && Array.IndexOf(kinds, o.ObjectKind) < 0)) continue;
            var d = Vector3.Distance(o.Position, from);
            if (d > bestD) continue;
            var name = o.Name.TextValue;
            if (!words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            best = o;
            bestD = d;
        }
        return best;
    }

    /// <summary>The NPC with that ENpcBase row, else one with that name (a subdivision's own Housing Merchant), nearest you within reach.</summary>
    public static IGameObject? NpcOrNamed(uint npcId, string name, float within) =>
        Npc(npcId, within) ?? (Me is { } me && name.Length > 0 ? Nearest(new[] { name }, me, within, ObjectKind.EventNpc) : null);

    /// <summary>The NPC with that ENpcBase row, nearest you within reach.</summary>
    public static IGameObject? Npc(uint npcId, float within)
    {
        if (Me is not { } me) return null;
        IGameObject? best = null;
        var bestD = within;
        foreach (var o in Services.Objects)
        {
            // (Only one that can be targeted and talked to: an NPC can have a copy that can't, 0.9.9.)
            if (o == null || o.ObjectKind != ObjectKind.EventNpc || o.BaseId != npcId || !o.IsTargetable) continue;
            var d = Vector3.Distance(o.Position, me);
            if (d <= bestD)
            {
                best = o;
                bestD = d;
            }
        }
        return best;
    }

    /// <summary>The names of the objects near you (for the log, when the one wanted isn't found).</summary>
    public static string NamesNear(float within)
    {
        if (Me is not { } me) return "nothing";
        var names = Services.Objects.Where(o => o != null && o.IsTargetable && o.ObjectKind is ObjectKind.EventObj or ObjectKind.EventNpc or ObjectKind.HousingEventObject
                                                && Vector3.Distance(o.Position, me) <= within)
            .Select(o => $"{o.Name.TextValue} ({o.ObjectKind}, {o.BaseId}, {Vector3.Distance(o.Position, me):0} y)").Distinct().Take(12).ToList();
        return names.Count == 0 ? "nothing" : string.Join("; ", names);
    }

    private static (nint Address, DateTime At, TimeSpan Wait) targeting;

    /// <summary>
    /// Targets an object before it's used, as you would (0.9.6: the errands used NPCs, the board, bells and
    /// doors without ever targeting them, which the game sends to the server). True while it's still to
    /// wait: it targets it now (if it isn't your target already), then a moment goes by (0.18 to 0.42 s,
    /// never the same) before it's used. Already your target for a while: false at once.
    /// </summary>
    public static bool TargetFirst(IGameObject o)
    {
        var now = DateTime.UtcNow;
        var current = Services.Targets.Target;
        // Targeted a moment ago: the moment first.
        if (targeting.Address == o.Address && now - targeting.At < targeting.Wait) return true;
        if (current == null || current.Address != o.Address)
        {
            // Tried already and it didn't take (an object the game won't target): used anyway, rather than
            // waiting on it until the step times out (0.9.9).
            if (targeting.Address == o.Address && now - targeting.At < TimeSpan.FromSeconds(5)) return false;
            Services.Targets.Target = o;
            targeting = (o.Address, now, TimeSpan.FromMilliseconds(Random.Shared.Next(180, 420)));
            return true;
        }
        return false;
    }

    /// <summary>Talks to (or uses) an object, as pressing on it does (the game's TargetSystem.InteractWithObject); it's your target then.</summary>
    public static bool Interact(IGameObject o)
    {
        var ts = TargetSystem.Instance();
        if (ts == null || o.Address == nint.Zero) return false;
        // Your target too, if it isn't yet (a press on something targets it).
        if (Services.Targets.Target?.Address != o.Address) Services.Targets.Target = o;
        ts->InteractWithObject((GameObjectStruct*)o.Address, true);
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // Menus and shops.

    /// <summary>The entries of an open SelectString or SelectIconString menu, or null when neither is open.</summary>
    public static (string Addon, List<string> Entries)? MenuEntries()
    {
        var s = (AddonSelectString*)GameUi.Ready(ErrandTable.SelectString);
        if (s != null) return (ErrandTable.SelectString, Read(s->PopupMenu.PopupMenu.EntryNames, s->PopupMenu.PopupMenu.EntryCount));
        var i = (AddonSelectIconString*)GameUi.Ready(ErrandTable.SelectIconString);
        if (i != null) return (ErrandTable.SelectIconString, Read(i->PopupMenu.PopupMenu.EntryNames, i->PopupMenu.PopupMenu.EntryCount));
        return null;
    }

    private static List<string> Read(InteropGenerator.Runtime.CStringPointer* names, int count)
    {
        var list = new List<string>();
        if (names == null) return list;
        for (var n = 0; n < count && n < 64; n++)
            list.Add(names[n].Value != null ? GameUi.Text(names[n].Value) : string.Empty);
        return list;
    }

    /// <summary>The first entry whose words start with one of <paramref name="words"/> (and none of <paramref name="not"/>), or -1.</summary>
    public static int Find(IReadOnlyList<string> entries, IReadOnlyList<string> words, IReadOnlyList<string>? not = null)
    {
        foreach (var w in words)
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i].Trim().TrimEnd('.');
                if (!e.StartsWith(w, StringComparison.OrdinalIgnoreCase)) continue;
                if (not != null && not.Any(x => e.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;
                return i;
            }
        return -1;
    }

    private static readonly Dictionary<uint, List<string>> ShopNames = new();

    /// <summary>The names of a gil shop's items, in the game's order (every item, wanted or not).</summary>
    public static List<string> ShopItemNames(uint shopId)
    {
        if (ShopNames.TryGetValue(shopId, out var list)) return list;
        list = new List<string>();
        if (Services.Data.GetSubrowExcelSheet<GilShopItem>().TryGetRow(shopId, out var rows))
            foreach (var r in rows)
                if (r.Item.RowId != 0) list.Add(ItemName(r.Item.RowId));
        ShopNames[shopId] = list;
        return list;
    }

    /// <summary>
    /// The row of the open shop window that sells an item: its name among the shop's item names as the
    /// window lists them (its text values, in order, that are the shop's items: those it doesn't show,
    /// behind a quest, aren't there). -1 when it isn't shown.
    /// </summary>
    public static int ShopRow(uint shopId, uint itemId)
    {
        var wanted = ItemName(itemId);
        if (wanted.Length == 0) return -1;
        var names = new HashSet<string>(ShopItemNames(shopId));
        var row = 0;
        foreach (var (_, text) in GameUi.TextValues(ErrandTable.Shop).OrderBy(kv => kv.Key))
        {
            if (!names.Contains(text)) continue;
            if (text == wanted) return row;
            row++;
        }
        return -1;
    }

    // ---------------------------------------------------------------------------------------------
    // Retainers.

    /// <summary>The retainer's row in the summoning bell's list (its sorted order), or -1.</summary>
    public static int RetainerRow(ulong retainerId)
    {
        var rm = RetainerManager.Instance();
        if (rm == null || !rm->IsReady) return -1;
        for (uint i = 0; i < rm->GetRetainerCount(); i++)
        {
            var r = rm->GetRetainerBySortedIndex(i);
            if (r != null && r->RetainerId == retainerId) return (int)i;
        }
        return -1;
    }

    /// <summary>The retainer whose bags are open, or 0.</summary>
    public static ulong ActiveRetainer()
    {
        var rm = RetainerManager.Instance();
        if (rm == null || !rm->IsReady) return 0;
        var r = rm->GetActiveRetainer();
        return r == null ? 0 : r->RetainerId;
    }

    /// <summary>A slot in the open retainer's bags holding the item (the biggest stack first), or null.</summary>
    public static (InventoryType Container, int Slot, int Quantity)? RetainerSlot(uint itemId)
    {
        var mgr = InventoryManager.Instance();
        if (mgr == null) return null;
        (InventoryType, int, int)? best = null;
        foreach (var t in RetainerPages)
        {
            var c = mgr->GetInventoryContainer(t);
            if (c == null || !c->IsLoaded) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var item = c->GetInventorySlot(i);
                if (item == null || item->IsEmpty() || item->GetBaseItemId() != itemId) continue;
                var q = (int)item->GetQuantity();
                if (best == null || q > best.Value.Item3) best = (t, i, q);
            }
        }
        return best;
    }

    /// <summary>Opens the item menu on a slot of the retainer's bags, owned by its open bag window.</summary>
    public static bool OpenRetainerItemMenu(InventoryType container, int slot)
    {
        var agent = AgentInventoryContext.Instance();
        if (agent == null) return false;
        uint owner = 0;
        foreach (var name in ErrandTable.RetainerInventory)
        {
            var a = GameUi.Ready(name);
            if (a != null)
            {
                owner = a->Id;
                break;
            }
        }
        if (owner == 0) return false;
        agent->OpenForItemSlot(container, slot, 0, owner);
        return true;
    }

    public static bool RetainerBagsOpen => ErrandTable.RetainerInventory.Any(GameUi.IsOpen);

    /// <summary>Has the game sent the open retainer's bags (any of their pages)?</summary>
    public static bool RetainerPagesLoaded()
    {
        var mgr = InventoryManager.Instance();
        if (mgr == null) return false;
        foreach (var t in RetainerPages)
        {
            var c = mgr->GetInventoryContainer(t);
            if (c != null && c->IsLoaded) return true;
        }
        return false;
    }

    /// <summary>The retainer's row in the bell's list by their name, or -1 (the run file names retainers).</summary>
    public static int RetainerRowByName(string name)
    {
        var rm = RetainerManager.Instance();
        if (rm == null || !rm->IsReady) return -1;
        for (uint i = 0; i < rm->GetRetainerCount(); i++)
        {
            var r = rm->GetRetainerBySortedIndex(i);
            if (r != null && string.Equals(r->NameString, name, StringComparison.OrdinalIgnoreCase)) return (int)i;
        }
        return -1;
    }

    /// <summary>The name of the retainer in row <paramref name="row"/> of the bell's list, or null.</summary>
    public static string? RetainerNameAt(int row)
    {
        var rm = RetainerManager.Instance();
        if (rm == null || !rm->IsReady || row < 0 || row >= rm->GetRetainerCount()) return null;
        var r = rm->GetRetainerBySortedIndex((uint)row);
        return r == null ? null : r->NameString;
    }

    /// <summary>Whether the game has sent your retainers' list this session (it does at a bell).</summary>
    public static bool RetainerListKnown
    {
        get
        {
            var rm = RetainerManager.Instance();
            return rm != null && rm->IsReady && rm->GetRetainerCount() > 0;
        }
    }

    /// <summary>Your retainers' names by id, from the game's list (empty until it's been sent this session).</summary>
    public static Dictionary<ulong, string> RetainerNames()
    {
        var names = new Dictionary<ulong, string>();
        var rm = RetainerManager.Instance();
        if (rm == null || !rm->IsReady) return names;
        foreach (ref var r in rm->Retainers)
            if (r.RetainerId != 0 && r.NameString.Length > 0) names[r.RetainerId] = r.NameString;
        return names;
    }

    /// <summary>The name of the retainer whose windows are open, or null.</summary>
    public static string? ActiveRetainerName()
    {
        var rm = RetainerManager.Instance();
        if (rm == null || !rm->IsReady) return null;
        var r = rm->GetActiveRetainer();
        return r == null || r->RetainerId == 0 ? null : r->NameString;
    }

    /// <summary>A slot in your bags holding the item (the biggest stack first, of the quality asked), or null.</summary>
    public static (InventoryType Container, int Slot, int Quantity)? BagSlot(uint itemId, bool? hq = null)
    {
        var mgr = InventoryManager.Instance();
        if (mgr == null) return null;
        (InventoryType, int, int)? best = null;
        foreach (var t in Bags)
        {
            var c = mgr->GetInventoryContainer(t);
            if (c == null || !c->IsLoaded) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var item = c->GetInventorySlot(i);
                if (item == null || item->IsEmpty() || item->GetBaseItemId() != itemId) continue;
                if (hq is { } wantHq && item->IsHighQuality() != wantHq) continue;
                var q = (int)item->GetQuantity();
                if (best == null || q > best.Value.Item3) best = (t, i, q);
            }
        }
        return best;
    }

    /// <summary>Your four bags.</summary>
    public static readonly InventoryType[] Bags =
    {
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
    };

    /// <summary>
    /// Opens the item menu on a slot of your bags, owned by your open bag window (with a retainer's bags
    /// open, its menu has "Entrust to Retainer"). Not yet seen working: held for you until trusted.
    /// </summary>
    public static bool OpenBagItemMenu(InventoryType container, int slot)
    {
        var agent = AgentInventoryContext.Instance();
        if (agent == null) return false;
        var owner = GameUi.BagWindowId();
        if (owner == 0) return false;
        agent->OpenForItemSlot(container, slot, 0, owner);
        return true;
    }

    /// <summary>How many high-quality copies of an item your bags hold.</summary>
    public static int HqCount(uint itemId)
    {
        var mgr = InventoryManager.Instance();
        return mgr == null ? 0 : mgr->GetInventoryItemCount(itemId, true, false, false, 0);
    }

    /// <summary>The data centre of your home world ("Light"), or empty.</summary>
    public static string DataCentre() =>
        Services.PlayerState.IsLoaded ? Services.PlayerState.HomeWorld.ValueNullable?.DataCenter.ValueNullable?.Name.ExtractText() ?? string.Empty : string.Empty;

    private static volatile Dictionary<string, uint>? itemsByName;

    /// <summary>Reads every item's name once, off the framework thread (for reading the sale history by names).</summary>
    public static void WarmUpNames() => System.Threading.Tasks.Task.Run(() =>
    {
        try
        {
            var names = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Services.Data.GetExcelSheet<Item>())
            {
                var n = item.Name.ExtractText();
                if (n.Length > 0) names.TryAdd(n, item.RowId);
            }
            itemsByName = names;
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "Market Ledger couldn't read the items' names");
            itemsByName = new Dictionary<string, uint>();
        }
    });

    /// <summary>An item by its name in your client's language, or 0 (none, or the names aren't read yet).</summary>
    public static uint ItemByName(string name) =>
        itemsByName is { } names && names.TryGetValue(name, out var id) ? id : 0;

    private static Dictionary<string, uint>? territoriesByPlace;

    /// <summary>The territory (with an aetheryte) whose place name is <paramref name="place"/> ("Mist", "Limsa Lominsa Lower Decks"), or 0.</summary>
    public static uint TerritoryOfPlace(string? place)
    {
        if (string.IsNullOrWhiteSpace(place)) return 0;
        if (territoriesByPlace == null)
        {
            territoriesByPlace = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in Services.Data.GetExcelSheet<TerritoryType>())
            {
                var n = t.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
                if (n.Length > 0 && AetheryteOf(t.RowId) != 0) territoriesByPlace.TryAdd(n, t.RowId);
            }
        }
        return territoriesByPlace.TryGetValue(place.Trim(), out var id) ? id : 0;
    }

    /// <summary>The place name of a territory ("Mist"), or empty.</summary>
    public static string PlaceName(uint territory) =>
        Services.Data.GetExcelSheet<TerritoryType>().TryGetRow(territory, out var t) ? t.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty : string.Empty;

    /// <summary>The territory's own name ("s1h1"), for telling residential wards.</summary>
    public static string TerritoryName(uint territory) =>
        Services.Data.GetExcelSheet<TerritoryType>().TryGetRow(territory, out var t) ? t.Name.ExtractText() : string.Empty;

    // ---------------------------------------------------------------------------------------------
    // The market board.

    /// <summary>A listing as the board shows it on this world (its row in the listings shown).</summary>
    public readonly record struct BoardListing(int Row, ulong ListingId, uint UnitPrice, int Quantity, uint TotalTax, bool Hq);

    /// <summary>The item whose listings the board has (or is loading), or 0.</summary>
    public static uint ListingsItem()
    {
        var p = InfoProxyItemSearch.Instance();
        return p == null ? 0 : p->SearchItemId;
    }

    /// <summary>
    /// Where <paramref name="name"/> is among the board's search results, as the search's window holds
    /// them (its text values, in order, that read like the name searched for), or -1. The search's own
    /// words may be among them: when the name is there twice, the first is taken to be the search box.
    /// What's opened is checked against the item before anything is bought.
    /// </summary>
    public static int SearchResultIndex(string name, out List<string> seen)
    {
        seen = GameUi.TextValues(ErrandTable.ItemSearch).OrderBy(kv => kv.Key)
            .Select(kv => kv.Value.Trim())
            .Where(t => t.Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var exact = new List<int>();
        for (var i = 0; i < seen.Count; i++)
            if (string.Equals(seen[i], name, StringComparison.OrdinalIgnoreCase)) exact.Add(i);
        if (exact.Count == 0) return -1;
        if (exact.Count == 1) return exact[0];
        // The search box's words first: the results are what follow it.
        return exact[1] - 1;
    }

    /// <summary>The listings the board has loaded for an item (in the order shown), or null while it isn't that item's or they're still loading.</summary>
    public static List<BoardListing>? Listings(uint itemId)
    {
        // As received (Dalamud's market board service), once a batch has settled. The game's item search
        // proxy only when that service couldn't be listened to: it said "loaded" before the listings
        // were (0.7.11: an empty list at once, then "closed").
        if (BoardListings.Hooked) return BoardListings.For(itemId, TimeSpan.FromMilliseconds(600));
        var p = InfoProxyItemSearch.Instance();
        if (p == null || p->SearchItemId != itemId || p->WaitingForListings) return null;
        var list = new List<BoardListing>();
        var span = p->Listings;
        var count = (int)Math.Min(p->ListingCount, (uint)span.Length);
        for (var i = 0; i < count; i++)
        {
            ref var l = ref span[i];
            if (l.ItemId != itemId || l.Quantity == 0) continue;
            list.Add(new BoardListing(i, l.ListingId, l.UnitPrice, (int)l.Quantity, l.TotalTax, l.IsHqItem));
        }
        return list;
    }
}
