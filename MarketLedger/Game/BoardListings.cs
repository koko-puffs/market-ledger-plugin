using System;
using System.Collections.Generic;
using Dalamud.Game.Network.Structures;

namespace MarketLedger.Game;

/// <summary>
/// The market board's listings for the item whose listings are open, as the game receives them
/// (Dalamud's market board service: each batch of up to ten, in the order the list shows them). Housing
/// Studio's plugin read them from the game's item search proxy first, which never said they'd come.
/// Each listing's row is its place in that order.
/// </summary>
public static class BoardListings
{
    private static readonly object Gate = new();
    private static readonly List<InGame.BoardListing> Received = new();
    private static uint item;
    private static DateTime lastAt = DateTime.MinValue;
    private static uint askedItem;
    private static DateTime askedAt = DateTime.MaxValue;
    private static bool hooked;
    private static bool answered;

    /// <summary>Listening to the market board service (else the game's item search proxy is read).</summary>
    public static bool Hooked => hooked;

    public static void Hook()
    {
        if (hooked) return;
        try
        {
            Services.MarketBoard.OfferingsReceived += OnOfferings;
            hooked = true;
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "Couldn't listen to the market board's listings");
        }
    }

    public static void Unhook()
    {
        if (!hooked) return;
        Services.MarketBoard.OfferingsReceived -= OnOfferings;
        hooked = false;
    }

    /// <summary>Listings of this item are about to be asked for: what came for another is forgotten.</summary>
    public static void Expect(uint itemId)
    {
        lock (Gate)
        {
            item = itemId;
            Received.Clear();
            answered = false;
            lastAt = DateTime.MinValue;
            askedItem = itemId;
            askedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// A listing was bought (or is gone): taken out, and the rows after it moved up one, as the board's
    /// own list does; a list the board sends again after the purchase replaces it anyway.
    /// </summary>
    public static void Gone(ulong listingId)
    {
        lock (Gate)
        {
            var i = Received.FindIndex(r => r.ListingId == listingId);
            if (i < 0) return;
            Received.RemoveAt(i);
            for (var k = i; k < Received.Count; k++) Received[k] = Received[k] with { Row = Received[k].Row - 1 };
        }
    }

    /// <summary>When this item's listings were last asked for (<see cref="DateTime.MaxValue"/> if they weren't).</summary>
    public static DateTime AskedAt(uint itemId)
    {
        lock (Gate) return askedItem == itemId ? askedAt : DateTime.MaxValue;
    }

    /// <summary>
    /// The listings to choose from while the item's listings window is open: as received, or none when
    /// nothing came 6 s after they were asked for; null when the window isn't open (or they're coming).
    /// </summary>
    public static List<InGame.BoardListing>? Open(uint itemId)
    {
        if (!MarketLedger.Automation.GameUi.IsOpen(MarketLedger.Automation.ErrandTable.ItemSearchResult)) return null;
        if (InGame.Listings(itemId) is { } l) return l;
        // Nothing came at all: none listed after 10 s (a slow or busy board took 4 s in Housing Studio's runs).
        return (DateTime.UtcNow - AskedAt(itemId)).TotalSeconds > 10 ? new List<InGame.BoardListing>() : null;
    }

    /// <summary>
    /// The item's listings once none has come for <paramref name="settle"/> (they come in batches), or
    /// null while none has come (or they're another item's).
    /// </summary>
    public static List<InGame.BoardListing>? For(uint itemId, TimeSpan settle)
    {
        lock (Gate)
        {
            // An answer with nothing in it is "none listed" (as is no answer at all, after a while: Open).
            if (item != itemId || (Received.Count == 0 && !answered) || DateTime.UtcNow - lastAt < settle) return null;
            return new List<InGame.BoardListing>(Received);
        }
    }

    private static void OnOfferings(IMarketBoardCurrentOfferings offerings)
    {
        lock (Gate)
        {
            // A request's batches come together; one coming later is the list sent again (after a
            // purchase, say), whose rows have moved: it's taken afresh.
            if (DateTime.UtcNow - lastAt > TimeSpan.FromSeconds(1.5)) Received.Clear();
            answered = true;
            foreach (var l in offerings.ItemListings)
            {
                if (l.ItemId != item)
                {
                    // Another item's listings (you opened another): they're what's open now.
                    item = l.ItemId;
                    Received.Clear();
                }
                if (l.ItemQuantity == 0 || Received.Exists(r => r.ListingId == l.ListingId)) continue;
                Received.Add(new InGame.BoardListing(Received.Count, l.ListingId, l.PricePerUnit, (int)l.ItemQuantity, l.TotalTax, l.IsHq));
            }
            lastAt = DateTime.UtcNow;
        }
    }
}
