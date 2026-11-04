using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using MarketLedger.Automation;
using MarketLedger.Core;

namespace MarketLedger.Game;

/// <summary>
/// A retainer's market, read while its windows are open (read only; nothing is clicked): what it has
/// for sale when its sell list is open, and its sales when its sale history is. Framework thread.
/// </summary>
/// <remarks>
/// The listings: the game's <c>RetainerMarket</c> container (<c>InventoryType.RetainerMarket</c>, up to
/// 20 slots) and each slot's price (<c>InventoryManager.GetRetainerMarketPrice(slot)</c>), as Allagan
/// Market (Critical-Impact/AllaganMarket, <c>Services/RetainerMarketService.cs</c> and
/// <c>MarketPriceUpdaterService.cs</c>) reads them, while <c>RetainerSellList</c> is open.
/// The sales: the <c>RetainerHistory</c> window, which neither FFXIVClientStructs nor Dalamud describe;
/// its rows are found by their shape (<see cref="SaleHistory"/>). Not yet checked in the game: when it's
/// open and nothing reads, its values go to the log once, so it can be fixed from a /xllog.
/// </remarks>
public static unsafe class RetainerMarket
{
    /// <summary>The retainer whose sell list is open, and what it lists; null when it isn't open (or not loaded).</summary>
    public static (string Retainer, List<ListingRow> Rows)? Listings()
    {
        if (!GameUi.IsOpen(ErrandTable.RetainerSellList) || InGame.ActiveRetainerName() is not { } retainer) return null;
        var mgr = InventoryManager.Instance();
        if (mgr == null) return null;
        var c = mgr->GetInventoryContainer(InventoryType.RetainerMarket);
        if (c == null || !c->IsLoaded) return null;
        var rows = new List<ListingRow>();
        for (var i = 0; i < c->Size && i < 20; i++)
        {
            var item = c->GetInventorySlot(i);
            if (item == null || item->IsEmpty()) continue;
            var price = (long)mgr->GetRetainerMarketPrice((short)i);
            rows.Add(new ListingRow(item->GetBaseItemId(), item->IsHighQuality(), (int)item->GetQuantity(), price));
        }
        return (retainer, rows);
    }

    private static bool dumped;

    /// <summary>The retainer whose sale history is open, and its sales as read; null when it isn't open.</summary>
    public static (string Retainer, List<SaleRow> Rows)? Sales()
    {
        if (!GameUi.IsOpen(ErrandTable.RetainerHistory) || InGame.ActiveRetainerName() is not { } retainer) return null;
        var values = GameUi.Values(ErrandTable.RetainerHistory);
        var rows = SaleHistory.Parse(retainer, values, InGame.ItemByName, DateTime.UtcNow);
        if (rows.Count == 0 && values.Count > 0 && !dumped)
        {
            dumped = true;
            Services.Log.Information($"Market Ledger couldn't read the sale history (send this to Koko): {GameUi.DumpValues(ErrandTable.RetainerHistory, 400)}");
        }
        return (retainer, rows);
    }
}
