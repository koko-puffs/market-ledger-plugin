using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MarketLedger.Core;
using Dalamud.Plugin.Ipc;

namespace MarketLedger.Ipc;

/// <summary>
/// AutoRetainer (PunishXIV/AutoRetainer), through its IPC: held out of the way while a run goes (its
/// "suppressed" state, which stops it opening retainers by itself), whether it's busy, and your
/// retainers' names. It has no IPC for taking things from or giving them to a retainer, so the run
/// does that at the bell itself (<see cref="Automation.ErrandTable"/>). Every call is wrapped. Framework thread.
/// </summary>
/// <remarks>
/// Verified in AutoRetainer's source (commit cc020df, 28 Aug 2026): <c>AutoRetainer.GetSuppressed</c> → bool
/// and <c>AutoRetainer.SetSuppressed</c> (bool) (<c>AutoRetainer/Modules/IPC.cs</c>, lines 22–23),
/// <c>AutoRetainer.GetOfflineCharacterData</c> (ulong) → its OfflineCharacterData (line 27; subscribed as
/// object, which Dalamud's <c>CallGateChannel.ConvertObject</c> hands over as JSON (a Newtonsoft JObject):
/// <c>RetainerData</c>, each <c>Name</c> and <c>RetainerID</c>, read by <see cref="Holdings.RetainerNamesFromJson"/>), and <c>AutoRetainer.PluginState.IsBusy</c> → bool
/// (<c>AutoRetainer/Modules/EzIPCManagers/IPC_PluginState.cs</c>, line 15; Housing Studio's plugin asked
/// "AutoRetainer.IsBusy", which isn't published).
/// </remarks>
public sealed class AutoRetainer
{
    public const string InternalName = "AutoRetainer";

    private readonly IpcGuard guard = Plugins.Guard(HelperNames.AutoRetainer);
    private readonly ICallGateSubscriber<bool> getSuppressed;
    private readonly ICallGateSubscriber<bool, object> setSuppressed;
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<ulong, object> characterData;
    private bool heldByUs;

    public AutoRetainer()
    {
        var pi = Services.PluginInterface;
        getSuppressed = pi.GetIpcSubscriber<bool>("AutoRetainer.GetSuppressed");
        setSuppressed = pi.GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed");
        isBusy = pi.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy");
        characterData = pi.GetIpcSubscriber<ulong, object>("AutoRetainer.GetOfflineCharacterData");
    }

    public static string Calls => "GetSuppressed · SetSuppressed · PluginState.IsBusy · GetOfflineCharacterData";

    public bool Installed => Plugins.Loaded(InternalName);

    public string? Version => Plugins.Version(InternalName);

    /// <summary>Holds AutoRetainer (unless it's held already). False when it didn't answer.</summary>
    public bool Hold()
    {
        if (heldByUs) return true;
        var already = guard.Call<bool?>("GetSuppressed", () => getSuppressed.InvokeFunc(), null);
        if (already == null) return false;
        if (already == true) return true;
        if (!guard.Do("SetSuppressed", () => setSuppressed.InvokeAction(true))) return false;
        heldByUs = true;
        return true;
    }

    /// <summary>Lets AutoRetainer carry on, if <see cref="Hold"/> held it.</summary>
    public void LetBe()
    {
        if (!heldByUs) return;
        heldByUs = false;
        guard.Do("SetSuppressed", () => setSuppressed.InvokeAction(false));
    }

    public bool Held => heldByUs;

    /// <summary>AutoRetainer is doing something; false when it doesn't answer.</summary>
    public bool Busy => guard.Call("PluginState.IsBusy", () => isBusy.InvokeFunc(), false);

    private (ulong ContentId, DateTime Until, Dictionary<ulong, string> Names) namesCache = (0, DateTime.MinValue, new());

    /// <summary>
    /// Your retainers' names by id, as AutoRetainer keeps them for a character (empty when it doesn't say);
    /// asked at most every 5 minutes (Dalamud writes its whole character record out as JSON to hand it over).
    /// </summary>
    public Dictionary<ulong, string> RetainerNames(ulong contentId)
    {
        var now = DateTime.UtcNow;
        if (namesCache.ContentId == contentId && now < namesCache.Until) return namesCache.Names;
        var names = new Dictionary<ulong, string>();
        guard.Do("GetOfflineCharacterData", () =>
        {
            var data = characterData.InvokeFunc(contentId);
            // Its type (AutoRetainerAPI's OfflineCharacterData) isn't this plugin's to name, so Dalamud converts
            // it for an object subscriber: through JSON, to a Newtonsoft JObject, whose text is read here. Read
            // by reflection too, should a Dalamud ever hand over the object itself.
            if (Read(data, "RetainerData") is IEnumerable retainers and not string)
            {
                foreach (var r in retainers)
                    if (r != null && Read(r, "RetainerID") is ulong id && id != 0 && Read(r, "Name") is string { Length: > 0 } name)
                        names[id] = name;
            }
            else if (data != null)
            {
                foreach (var (id, name) in Holdings.RetainerNamesFromJson(data.ToString())) names[id] = name;
            }
        });
        namesCache = (contentId, now + TimeSpan.FromMinutes(5), names);
        return names;
    }

    public (int Answered, int Asked) Probe()
    {
        guard.Reset();
        var answered = 0;
        if (guard.Call<bool?>("GetSuppressed", () => getSuppressed.InvokeFunc(), null) != null) answered++;
        if (guard.Call<bool?>("PluginState.IsBusy", () => isBusy.InvokeFunc(), null) != null) answered++;
        return (answered, 2);
    }

    /// <summary>A public field's or property's value by name, or null.</summary>
    private static object? Read(object? from, string name)
    {
        if (from == null) return null;
        var t = from.GetType();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
        if (t.GetField(name, flags) is { } f) return f.GetValue(from);
        if (t.GetProperty(name, flags) is { } p && p.GetIndexParameters().Length == 0) return p.GetValue(from);
        return null;
    }
}
