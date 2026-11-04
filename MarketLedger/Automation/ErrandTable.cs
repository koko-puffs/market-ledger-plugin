using System.Collections.Generic;
using MarketLedger.Core;

namespace MarketLedger.Automation;

/// <summary>
/// Every game window and click a run uses at a vendor, a summoning bell and the market board, in one
/// place, with where each came from and how sure it is: Housing Studio's plugin's errand table, whose
/// values were recorded doing each by hand in the game (its Recording 2: a Material Supplier's and a
/// housing merchant's purchase, a summoning bell, a retainer's item taken, a market board listing
/// bought). The ones still marked unconfirmed (<see cref="UiCall.Verified"/> false, or a step keyed
/// "guess") hold the run before the click until you press Resume, until you switch on Settings → Trust
/// the new clicks, having watched them work.
///
/// Sources:
/// <list type="bullet">
/// <item>[ECommons] NightmareXIV/ECommons: SelectString and SelectIconString pick an entry with
/// (index); SelectYesno's yes is (0).</item>
/// <item>[Recording 2] Housing Studio's: seen sent by the game, by hand.</item>
/// <item>[Guess] Written from what other plugins are known to send, not checked against a recording.</item>
/// </list>
/// Words are matched in English only so far: in another client's language a menu entry or object isn't
/// found, and the run pauses and says which.
/// </summary>
public static class ErrandTable
{
    // ---------- vendors ----------

    /// <summary>A gil shop's window (the NPC's items, with Purchase). [ECommons: AddonMaster "Shop"]</summary>
    public const string Shop = "Shop";

    /// <summary>
    /// Buy <c>count</c> of the item in row <c>row</c> of the gil shop (0 is the list's first, as shown).
    /// The game then asks "Purchase 1 riviera table for 2,000 gil?" (OK / Cancel). [Recording 2:
    /// [i:0, i:14, i:1, empty] for a lump of beeswax, row 14 of the Material Supplier's; [i:0, i:4,
    /// i:1, empty] for a Riviera Table, row 4 of the housing merchant's Tables, Food. [i:6, row] and
    /// [i:7, row] are the mouse going over a row.]
    /// </summary>
    public static readonly UiCall ShopBuy = new(Shop, "i:0 i:{row} i:{count} empty", true, "Recording 2");

    /// <summary>The menu some NPCs open first ("Purchase Furnishings (Tier 1)", "Purchase Items"). [ECommons]</summary>
    public const string SelectString = "SelectString";

    /// <summary>The same with icons (the housing vendors' menus). [ECommons]</summary>
    public const string SelectIconString = "SelectIconString";

    /// <summary>Picks a menu's entry (0 is the first). [ECommons: Callback.Fire(addon, true, index)]</summary>
    public static readonly UiCall MenuPick = new(SelectString, "i:{index}", true, "ECommons");

    /// <summary>[Recording 2: the Material Supplier's "Purchase Items" [i:0]; the housing merchant's "Purchase Furnishings (Tables, Food)", the fourth, [i:3]]</summary>
    public static readonly UiCall IconMenuPick = new(SelectIconString, "i:{index}", true, "ECommons, Recording 2");

    /// <summary>An NPC's (or a retainer's) words.</summary>
    public const string Talk = "Talk";

    /// <summary>
    /// Goes on to the next of an NPC's words, as clicking them does. [Recording 2: Talk [] (no values),
    /// each time a retainer's greeting was clicked through.] If the words don't move on, you're asked.
    /// </summary>
    public static readonly UiCall TalkNext = new(Talk, "", true, "Recording 2");

    // ---------- retainers ----------

    /// <summary>The summoning bell's list of retainers. [AutoRetainer]</summary>
    public const string RetainerList = "RetainerList";

    /// <summary>
    /// Picks the retainer in row <c>index</c> of the list. [Recording 2: [i:2, u:5, empty, empty] for the
    /// sixth; [i:3, empty, u:row, empty] and [i:4] are the mouse going over a row and off it.]
    /// </summary>
    public static readonly UiCall RetainerPick = new(RetainerList, "i:2 u:{index} empty empty", true, "Recording 2");

    /// <summary>The retainer's menu entry that opens their bags ("Entrust or withdraw items."), matched by its words (English).</summary>
    public static readonly string[] RetainerItemsWords = { "Entrust or withdraw items" };

    /// <summary>The retainer's menu entry that ends the talk ("Quit."), matched by its words.</summary>
    public static readonly string[] RetainerQuitWords = { "Quit" };

    /// <summary>The retainer's bag windows (as the plugin's own retainer record reads). [Retainers.cs]</summary>
    public static readonly string[] RetainerInventory = { "InventoryRetainer", "InventoryRetainerLarge" };

    /// <summary>
    /// The item menu's entry that takes a stack from the retainer to your bags, matched by its words
    /// (never "Retrieve Quantity"). [Recording 2: the menu reads "Retrieve from Retainer | Have Retainer
    /// Sell Items | Dye | Search for Item | …", and [i:0, i:0, u:0] chose it.] How the plugin opens the
    /// menu (the game's own item menu for that slot) is still a guess.
    /// </summary>
    public static readonly string[] RetrieveWords = { "Retrieve from Retainer", "Retrieve All", "Retrieve" };

    /// <summary>The words of an entry that must never be chosen for a retrieve (it asks how many).</summary>
    public static readonly string[] RetrieveQuantityWords = { "Quantity" };

    // ---------- the market board ----------

    /// <summary>The board's search window, open once the board is used. [ECommons: "ItemSearch"]</summary>
    public const string ItemSearch = "ItemSearch";

    /// <summary>An item's listings on this world. [ECommons: "ItemSearchResult"]</summary>
    public const string ItemSearchResult = "ItemSearchResult";

    /// <summary>
    /// Buys the listing in row <c>index</c> of the listings shown; the board then asks "Purchase 1
    /// hedge-trimmed flower bed for 22,107 gil (1,052 fee included)?" (OK / Cancel). [Recording 2:
    /// [i:2, i:0] for the first listing.] The search before it (not done by the plugin) was
    /// ItemSearch [i:9, empty, empty, s:"hedge", s:"hedge", …], then [i:5, i:8] for the ninth result.
    /// </summary>
    public static readonly UiCall ListingBuy = new(ItemSearchResult, "i:2 i:{index}", true, "Recording 2");

    /// <summary>
    /// The board's search by words: ItemSearch [i:9, empty, empty, s:words, s:words, empty, empty,
    /// empty]. [Recording 2, searching "hedge"] Sent with <see cref="GameUi.FireValues"/> (it holds text).
    /// </summary>
    public const int SearchByWords = 9;

    /// <summary>Opens the listings of the search's result in row <c>index</c>. [Recording 2: [i:5, i:8] for the ninth]</summary>
    public static readonly UiCall SearchResultPick = new(ItemSearch, "i:5 i:{index}", true, "Recording 2; 0.7.13's recordings: [i:5, i:0] opens the first result");

    // ---------- objects by name ----------

    /// <summary>EObjName rows: a summoning bell (2000401, as AutoRetainer looks for it) and the board's (a guess); their names in your client's language are read from these.</summary>
    public const uint SummoningBellRow = 2000401;

    public const uint MarketBoardRow = 2000402;

    public static readonly string[] SummoningBellWords = { "Summoning Bell" };

    public static readonly string[] MarketBoardWords = { "Market Board" };

    // ---------- entrusting ----------

    /// <summary>
    /// The item menu's entry, on a stack in your bags while a retainer's bags are open, that gives the
    /// whole stack to the retainer ("Entrust to Retainer"; never "Entrust Quantity", which asks how
    /// many). [Guess: the game's English words; the menu is opened the way the retrieve's is, from your
    /// bag window, and both are held for you until trusted.]
    /// </summary>
    public static readonly string[] EntrustWords = { "Entrust to Retainer", "Entrust" };

    /// <summary>The words of an entrust entry that must never be chosen (it asks how many).</summary>
    public static readonly string[] EntrustQuantityWords = { "Quantity" };

    // ---------- the retainer's market windows (read only) ----------

    /// <summary>The retainer's sell list ("Sell items in your inventory on the market"). [AllaganMarket: RetainerSellList]</summary>
    public const string RetainerSellList = "RetainerSellList";

    /// <summary>The retainer's sale history ("View sale history"). [Guess: the game's window name; read only]</summary>
    public const string RetainerHistory = "RetainerHistory";

    /// <summary>Every click a run makes, for Settings' list of windows and clicks.</summary>
    public static IReadOnlyList<UiCall> All => new[] { ShopBuy, MenuPick, IconMenuPick, TalkNext, RetainerPick, SearchResultPick, ListingBuy };
}
