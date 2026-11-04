using System;
using System.Collections.Generic;
using System.Linq;
using MarketLedger.Core;
using Dalamud.Plugin.Ipc;

namespace MarketLedger.Ipc;

/// <summary>
/// Allagan Tools (Critical-Impact/InventoryTools), through its IPC: what your retainers hold, as it last
/// saw (it keeps them between sessions), so the site knows without you opening each. Every call is
/// wrapped: missing, not ready or failing just means nothing from it. Framework thread.
/// </summary>
/// <remarks>
/// Verified in its source (<c>InventoryTools/IPC/IPCService.cs</c>, commit 70a9f41, 13 Sep 2026):
/// <c>AllaganTools.IsInitialized</c> → bool (line 592), <c>AllaganTools.GetCharactersOwnedByActive</c>
/// (bool includeOwner) → HashSet&lt;ulong&gt; (the active character's retainers' ids; line 574),
/// <c>AllaganTools.GetCharacterItems</c> (ulong) → HashSet&lt;ulong[]&gt; (line 571), each array the item's
/// numbers with its id first and its quantity second (as Housing Studio's plugin reads them).
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

    /// <summary>Each of the active character's retainers' counts (by retainer id), or none when it doesn't answer.</summary>
    public Dictionary<ulong, Dictionary<uint, int>> Retainers()
    {
        var result = new Dictionary<ulong, Dictionary<uint, int>>();
        if (!Installed || !guard.Call("IsInitialized", () => isInitialized.InvokeFunc(), false)) return result;
        var ids = guard.Call<HashSet<ulong>?>("GetCharactersOwnedByActive", () => ownedByActive.InvokeFunc(false), null);
        if (ids == null) return result;
        foreach (var id in ids)
        {
            var items = guard.Call<HashSet<ulong[]>?>("GetCharacterItems", () => characterItems.InvokeFunc(id), null);
            if (items == null) continue;
            var counts = Holdings.Count(items.Where(a => a is { Length: >= 2 }).Select(a => ((uint)Math.Min(a[0], uint.MaxValue), (long)Math.Min(a[1], long.MaxValue))));
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
