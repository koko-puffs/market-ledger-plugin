using MarketLedger.Core;
using Dalamud.Plugin.Ipc;

namespace MarketLedger.Ipc;

/// <summary>
/// Artisan (PunishXIV/Artisan), through its IPC: crafting a recipe a set number of times and watching it
/// do so. Every call is wrapped (<see cref="IpcGuard"/>): not installed, another version's signature or
/// a throw mean "can't", logged once. Framework thread.
/// </summary>
/// <remarks>
/// Verified in Artisan's source (<c>Artisan/IPC/IPC.cs</c>, <c>IPC.Init</c>, commit a6118e6, 31 Oct 2026):
/// <list type="bullet">
/// <item><c>Artisan.CraftItem</c> (ushort recipeId, int amount), an action: selects the recipe and starts
/// Endurance mode for that many crafts (line 51, <c>CraftX</c>).</item>
/// <item><c>Artisan.IsBusy</c> → bool: Endurance on, a list running, tasks queued, or mid-craft (line 52).</item>
/// <item><c>Artisan.IsListRunning</c>, <c>Artisan.IsListPaused</c> → bool; <c>Artisan.SetListPause</c> (bool) (lines 44–46).</item>
/// <item><c>Artisan.GetEnduranceStatus</c> → bool; <c>Artisan.SetEnduranceStatus</c> (bool) (lines 41–42).</item>
/// <item><c>Artisan.GetStopRequest</c> → bool; <c>Artisan.SetStopRequest</c> (bool) (lines 48–49). Setting it
/// true stops crafting; setting it false <em>resumes</em> it (Artisan's <c>ResumeCrafting</c>), so the plugin
/// reads it but stops Artisan with <c>SetEnduranceStatus(false)</c>, which is what the stop request does in
/// Endurance mode, without leaving the flag set (see DETAILS.md, "Crafting").</item>
/// </list>
/// </remarks>
public sealed class Artisan
{
    public const string InternalName = "Artisan";

    private readonly IpcGuard guard = Plugins.Guard(HelperNames.Artisan);
    private readonly ICallGateSubscriber<ushort, int, object> craftItem;
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<bool> isListRunning;
    private readonly ICallGateSubscriber<bool> isListPaused;
    private readonly ICallGateSubscriber<bool, object> setListPause;
    private readonly ICallGateSubscriber<bool> getEndurance;
    private readonly ICallGateSubscriber<bool, object> setEndurance;
    private readonly ICallGateSubscriber<bool> getStopRequest;

    public Artisan()
    {
        var pi = Services.PluginInterface;
        craftItem = pi.GetIpcSubscriber<ushort, int, object>("Artisan.CraftItem");
        isBusy = pi.GetIpcSubscriber<bool>("Artisan.IsBusy");
        isListRunning = pi.GetIpcSubscriber<bool>("Artisan.IsListRunning");
        isListPaused = pi.GetIpcSubscriber<bool>("Artisan.IsListPaused");
        setListPause = pi.GetIpcSubscriber<bool, object>("Artisan.SetListPause");
        getEndurance = pi.GetIpcSubscriber<bool>("Artisan.GetEnduranceStatus");
        setEndurance = pi.GetIpcSubscriber<bool, object>("Artisan.SetEnduranceStatus");
        getStopRequest = pi.GetIpcSubscriber<bool>("Artisan.GetStopRequest");
    }

    public static string Calls => "CraftItem · IsBusy · IsListRunning · GetEnduranceStatus · SetEnduranceStatus · GetStopRequest";

    public bool Installed => Plugins.Loaded(InternalName);

    public string? Version => Plugins.Version(InternalName);

    /// <summary>Starts crafting a recipe <paramref name="crafts"/> times (Endurance mode). False when Artisan couldn't be asked.</summary>
    public bool Craft(ushort recipe, int crafts) => guard.Do("CraftItem", () => craftItem.InvokeAction(recipe, crafts));

    /// <summary>Doing anything (null: it didn't answer).</summary>
    public bool? Busy => guard.Call<bool?>("IsBusy", () => isBusy.InvokeFunc(), null);

    /// <summary>Running one of its own lists (null: it didn't answer).</summary>
    public bool? ListRunning => guard.Call<bool?>("IsListRunning", () => isListRunning.InvokeFunc(), null);

    public bool? ListPaused => guard.Call<bool?>("IsListPaused", () => isListPaused.InvokeFunc(), null);

    public bool PauseList(bool pause) => guard.Do("SetListPause", () => setListPause.InvokeAction(pause));

    public bool? Endurance => guard.Call<bool?>("GetEnduranceStatus", () => getEndurance.InvokeFunc(), null);

    /// <summary>Endurance mode off: the crafting started by <see cref="Craft"/> stops after the craft under way.</summary>
    public bool StopEndurance() => guard.Do("SetEnduranceStatus", () => setEndurance.InvokeAction(false));

    /// <summary>Someone (Artisan itself, entering a duty; another plugin) asked Artisan to stop (null: it didn't answer).</summary>
    public bool? StopRequested => guard.Call<bool?>("GetStopRequest", () => getStopRequest.InvokeFunc(), null);

    /// <summary>The calls a check asks, and how many answered.</summary>
    public (int Answered, int Asked) Probe()
    {
        guard.Reset();
        var answered = 0;
        if (Busy != null) answered++;
        if (ListRunning != null) answered++;
        if (Endurance != null) answered++;
        if (StopRequested != null) answered++;
        return (answered, 4);
    }
}
