using MarketLedger.Core;

namespace MarketLedger.Automation;

/// <summary>
/// The game's general windows and clicks a run uses (the item menu, the yes/no box, the bags), with
/// where each came from: Housing Studio's plugin's table, whose values were seen working in the game
/// (its recordings 1 and 4, English client, patch 7.5, October 2026).
/// </summary>
public static class UiTable
{
    /// <summary>The menu that opens on an item. [Housing Studio: Recording 1; ECommons]</summary>
    public const string ContextMenu = "ContextMenu";

    /// <summary>The game's yes/no box. [ECommons]</summary>
    public const string YesNo = "SelectYesno";

    /// <summary>The bag windows, whichever layout you use; an item menu is opened "from" the open one.</summary>
    public static readonly string[] BagWindows = { "Inventory", "InventoryLarge", "InventoryExpansion" };

    /// <summary>Choose an item menu's entry. [Housing Studio: Recordings 1 and 2]</summary>
    public static readonly UiCall MenuChoose = new(ContextMenu, "i:0 i:{entry} u:0 empty empty", true, "Housing Studio, Recording 1");

    /// <summary>Close an item menu without choosing. [Housing Studio: Recording 4]</summary>
    public static readonly UiCall MenuCancel = new(ContextMenu, "i:0 i:-1 u:0 empty empty", true, "Housing Studio, Recording 4");

    /// <summary>Press Yes. [ECommons; Housing Studio: Recording 4]</summary>
    public static readonly UiCall YesNoYes = new(YesNo, "i:0", true, "Housing Studio, Recording 4");
}
