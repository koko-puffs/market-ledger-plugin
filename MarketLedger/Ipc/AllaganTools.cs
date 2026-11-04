using System;
using System.Collections.Generic;
using System.Linq;
using MarketLedger.Core;
using Dalamud.Plugin.Ipc;

namespace MarketLedger.Ipc;

/// <summary>
/// Allagan Tools (Critical-Impact/InventoryTools, internal name InventoryTools), through its IPC: what your
/// retainers hold, without opening each. Every call is wrapped (<see cref="IpcGuard"/>). Framework thread.
/// </summary>
/// <remarks>
/// Verified in its source (<c>InventoryTools/IPC/IPCService.cs</c>, commit 70a9f41):
/// <c>AllaganTools.IsInitialized</c> → bool (line 592), <c>AllaganTools.GetCharactersOwnedByActive</c>
/// (bool includeOwner) → HashSet&lt;ulong&gt; (line 574: the active character's retainers, and itself when
/// asked), <c>AllaganTools.GetCharacterItems</c> (ulong) → HashSet&lt;ulong[]&gt; (line 571: every item it
/// knows that character holds, each as CriticalCommonLib's <c>InventoryItem.ToNumeric()</c>: [0] container,
/// [1] slot, [2] item id, [3] quantity; read by <see cref="Holdings.FromAllagan"/>).
/// </remarks>
public sealed class AllaganTools
{
    public const string InternalName = "InventoryTools";

    private readonly IpcGuard guard = Plugins.Guard(HelperNames.AllaganTools);
    private readonly ICallGateSubscriber<bool> isInitialized;
    private readonly ICallGateSubscriber<bool, HashSet<ulong>> ownedByActive;
    private readonly ICallGateSubscriber<ulong, HashSet<ulong[]>> characterItems;

    public AllaganTools()
    {
        var pi = Services.PluginInterface;
        isInitialized = pi.GetIpcSubscriber<bool>("AllaganTools.IsInitialized");
        ownedByActive = pi.GetIpcSubscriber<bool, HashSet<ulong>>("AllaganTools.GetCharactersOwnedByActive");
        characterItems = pi.GetIpcSubscriber<ulong, HashSet<ulong[]>>("AllaganTools.GetCharacterItems");
    }

    public static string Calls => "IsInitialized · GetCharactersOwnedByActive · GetCharacterItems";

    public bool Installed => Plugins.Loaded(InternalName);

    public string? Version => Plugins.Version(InternalName);

    /// <summary>
    /// What each of the active character's retainers holds in its bags and crystals (by the retainer's id),
    /// as Allagan Tools last saw it. Empty when it isn't installed or ready.
    /// </summary>
    public Dictionary<ulong, Dictionary<uint, int>> Retainers()
    {
        var result = new Dictionary<ulong, Dictionary<uint, int>>();
        if (!Installed || !guard.Call("IsInitialized", () => isInitialized.InvokeFunc(), false)) return result;
        var ids = guard.Call<HashSet<ulong>?>("GetCharactersOwnedByActive", () => ownedByActive.InvokeFunc(false), null);
        if (ids == null) return result;
        foreach (var id in ids.Take(64))
        {
            var items = guard.Call<HashSet<ulong[]>?>("GetCharacterItems", () => characterItems.InvokeFunc(id), null);
            if (items == null) continue;
            var counts = Holdings.FromAllagan(items);
            if (counts.Count > 0) result[id] = counts;
        }
        return result;
    }

    public (int Answered, int Asked) Probe()
    {
        guard.Reset();
        return (guard.Call<bool?>("IsInitialized", () => isInitialized.InvokeFunc(), null) != null ? 1 : 0, 1);
    }
}
