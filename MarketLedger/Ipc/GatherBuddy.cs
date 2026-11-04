using System.Collections.Generic;
using MarketLedger.Core;
using Dalamud.Plugin.Ipc;

namespace MarketLedger.Ipc;

/// <summary>
/// GatherBuddy Reborn (FFXIV-CombatReborn/GatherBuddyReborn), through its IPC: its auto-gather, and
/// (where its IPC has them) its auto-gather lists. Every call is wrapped (<see cref="IpcGuard"/>).
/// Framework thread.
/// </summary>
/// <remarks>
/// Its IPC is <c>GatherBuddy/Plugin/GatherBuddyIpc.cs</c>, each method published by its own EzIPC as
/// <c>GatherBuddyReborn.Name</c> (<c>GatherBuddy/Plugin/EzIpc.cs</c>, the prefix its InternalName).
/// <para>Upstream (main, commit a204f2f, 20 Sep 2026; its IpcVersion 2): <c>Version</c> → int,
/// <c>Identify</c> (string) → uint, <c>IsAutoGatherEnabled</c> → bool, <c>GetAutoGatherStatusText</c> →
/// string, <c>SetAutoGatherEnabled</c> (bool), <c>IsAutoGatherWaiting</c> → bool. No lists: auto-gather
/// gathers whatever lists are enabled in its own window.</para>
/// <para>HenryBaylis/GatherBuddyReborn, branch <c>feature/autogather-list-ipc</c> (commit 240b220, 28 Sep
/// 2026; IpcVersion 5) adds the lists: <c>GetAutoGatherLists</c> → Dictionary&lt;string, bool&gt; (name →
/// enabled), <c>GetAutoGatherListItems</c> (string) → Dictionary&lt;uint, uint&gt;, <c>SetAutoGatherList</c>
/// (string name, Dictionary&lt;uint, uint&gt; item → quantity, string? description) → int added (makes the
/// list, disabled, or replaces its items), <c>SetAutoGatherListEnabled</c> (string, bool) → bool,
/// <c>DeleteAutoGatherList</c> (string) → bool. A list's quantity is how many to own in all
/// (<c>AutoGather.Gather.cs</c>: it gathers while <c>GetTotalCount() &lt; Quantity</c>), the same as a run
/// line's <c>need</c>.</para>
/// Which one is installed is found by asking <c>GetAutoGatherLists</c>: answered, the lists are used;
/// not, the fallback (DETAILS.md, "Gathering").
/// </remarks>
public sealed class GatherBuddy
{
    public const string InternalName = "GatherBuddyReborn";

    private readonly IpcGuard guard = Plugins.Guard(HelperNames.GatherBuddy);
    private readonly ICallGateSubscriber<int> version;
    private readonly ICallGateSubscriber<bool> isEnabled;
    private readonly ICallGateSubscriber<string> statusText;
    private readonly ICallGateSubscriber<bool, object> setEnabled;
    private readonly ICallGateSubscriber<bool> isWaiting;
    private readonly ICallGateSubscriber<Dictionary<string, bool>> getLists;
    private readonly ICallGateSubscriber<string, Dictionary<uint, uint>, string?, int> setList;
    private readonly ICallGateSubscriber<string, bool, bool> setListEnabled;
    private readonly ICallGateSubscriber<string, bool> deleteList;

    public GatherBuddy()
    {
        var pi = Services.PluginInterface;
        version = pi.GetIpcSubscriber<int>("GatherBuddyReborn.Version");
        isEnabled = pi.GetIpcSubscriber<bool>("GatherBuddyReborn.IsAutoGatherEnabled");
        statusText = pi.GetIpcSubscriber<string>("GatherBuddyReborn.GetAutoGatherStatusText");
        setEnabled = pi.GetIpcSubscriber<bool, object>("GatherBuddyReborn.SetAutoGatherEnabled");
        isWaiting = pi.GetIpcSubscriber<bool>("GatherBuddyReborn.IsAutoGatherWaiting");
        getLists = pi.GetIpcSubscriber<Dictionary<string, bool>>("GatherBuddyReborn.GetAutoGatherLists");
        setList = pi.GetIpcSubscriber<string, Dictionary<uint, uint>, string?, int>("GatherBuddyReborn.SetAutoGatherList");
        setListEnabled = pi.GetIpcSubscriber<string, bool, bool>("GatherBuddyReborn.SetAutoGatherListEnabled");
        deleteList = pi.GetIpcSubscriber<string, bool>("GatherBuddyReborn.DeleteAutoGatherList");
    }

    public static string Calls => "SetAutoGatherEnabled · IsAutoGatherEnabled · IsAutoGatherWaiting · GetAutoGatherStatusText; lists: GetAutoGatherLists · SetAutoGatherList · SetAutoGatherListEnabled · DeleteAutoGatherList";

    public bool Installed => Plugins.Loaded(InternalName);

    public string? PluginVersion => Plugins.Version(InternalName);

    /// <summary>Its IPC's own version (2 upstream, 5 with the lists), or null.</summary>
    public int? IpcVersion => guard.Call<int?>("Version", () => version.InvokeFunc(), null);

    public bool? Enabled => guard.Call<bool?>("IsAutoGatherEnabled", () => isEnabled.InvokeFunc(), null);

    public bool SetEnabled(bool on) => guard.Do("SetAutoGatherEnabled", () => setEnabled.InvokeAction(on));

    /// <summary>Auto-gather is on but has nothing to gather now (a timed node's window not open), or null.</summary>
    public bool? Waiting => guard.Call<bool?>("IsAutoGatherWaiting", () => isWaiting.InvokeFunc(), null);

    /// <summary>What auto-gather says it's doing ("Gathering...", "Teleporting..."), or null.</summary>
    public string? Status => guard.Call<string?>("GetAutoGatherStatusText", () => statusText.InvokeFunc(), null);

    /// <summary>Its auto-gather lists (name → enabled), or null when its IPC has no lists (upstream, so far).</summary>
    public Dictionary<string, bool>? Lists => guard.Call<Dictionary<string, bool>?>("GetAutoGatherLists", () => getLists.InvokeFunc(), null);

    /// <summary>Its IPC takes lists.</summary>
    public bool HasLists => Lists != null;

    /// <summary>Makes (or replaces the items of) a list: item → how many to own. How many items it took (-1: it couldn't be asked).</summary>
    public int SetList(string name, Dictionary<uint, uint> items, string description) =>
        guard.Call("SetAutoGatherList", () => setList.InvokeFunc(name, items, description), -1);

    /// <summary>Whether the list now has that state (false: not found, refused by its checks, or not asked).</summary>
    public bool SetListEnabled(string name, bool enabled) =>
        guard.Call("SetAutoGatherListEnabled", () => setListEnabled.InvokeFunc(name, enabled), false);

    public bool DeleteList(string name) => guard.Call("DeleteAutoGatherList", () => deleteList.InvokeFunc(name), false);

    public (int Answered, int Asked) Probe()
    {
        guard.Reset();
        var answered = 0;
        if (Enabled != null) answered++;
        if (Waiting != null) answered++;
        if (Status != null) answered++;
        return (answered, 3);
    }
}
