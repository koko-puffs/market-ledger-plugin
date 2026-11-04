using MarketLedger.Core;
using Dalamud.Plugin.Ipc;

namespace MarketLedger.Ipc;

/// <summary>
/// Lifestream (NightmareXIV/Lifestream), through its IPC: teleporting, a city's aethernet, a residential
/// district's ward, visiting another world on your data centre (Housing Studio's plugin's wrapper).
/// Every call is wrapped (<see cref="IpcGuard"/>). Framework thread.
/// </summary>
/// <remarks>
/// Verified in Lifestream's source (<c>Lifestream/IPC/IPCProvider.cs</c>, commit b363201, 16 Oct 2026;
/// ECommons' EzIPC, each published as "Lifestream.Name"): <c>ExecuteCommand</c> (string) (line 35),
/// <c>GoToHousingAddress</c> (AddressBookEntryTuple) (line 59), <c>IsBusy</c> → bool (line 65),
/// <c>Abort</c> (line 71), <c>CanVisitSameDC</c> (string) → bool (line 78), <c>ChangeWorld</c> (string) →
/// bool (line 102), <c>AethernetTeleport</c> (string) → bool (line 139), <c>Teleport</c> (uint aetheryte,
/// byte subIndex) → bool (line 241).
/// </remarks>
public sealed class Lifestream
{
    public const string InternalName = "Lifestream";

    private readonly IpcGuard guard = Plugins.Guard(HelperNames.Lifestream);
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<object> abort;
    private readonly ICallGateSubscriber<uint, byte, bool> teleport;
    private readonly ICallGateSubscriber<string, bool> changeWorld;
    private readonly ICallGateSubscriber<string, bool> canVisitSameDc;
    private readonly ICallGateSubscriber<string, object> executeCommand;
    private readonly ICallGateSubscriber<string, bool> aethernet;
    private readonly ICallGateSubscriber<(string Name, int World, int City, int Ward, int PropertyType, int Plot, int Apartment, bool ApartmentSubdivision, bool AliasEnabled, string Alias), object> goToAddress;

    public Lifestream()
    {
        var pi = Services.PluginInterface;
        isBusy = pi.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        abort = pi.GetIpcSubscriber<object>("Lifestream.Abort");
        teleport = pi.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");
        changeWorld = pi.GetIpcSubscriber<string, bool>("Lifestream.ChangeWorld");
        canVisitSameDc = pi.GetIpcSubscriber<string, bool>("Lifestream.CanVisitSameDC");
        executeCommand = pi.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand");
        aethernet = pi.GetIpcSubscriber<string, bool>("Lifestream.AethernetTeleport");
        goToAddress = pi.GetIpcSubscriber<(string Name, int World, int City, int Ward, int PropertyType, int Plot, int Apartment, bool ApartmentSubdivision, bool AliasEnabled, string Alias), object>(
            "Lifestream.GoToHousingAddress");
    }

    public static string Calls => "Teleport · AethernetTeleport · ChangeWorld · GoToHousingAddress · IsBusy · Abort";

    public bool Installed => Plugins.Loaded(InternalName);

    public string? Version => Plugins.Version(InternalName);

    /// <summary>Travelling. (When it doesn't answer: not busy, so the run's own timeouts decide.)</summary>
    public bool Busy => guard.Call("IsBusy", () => isBusy.InvokeFunc(), false);

    /// <summary>Stops whatever it's doing (safe to call whenever).</summary>
    public void Abort() => guard.Do("Abort", () => abort.InvokeAction());

    /// <summary>Teleports to an aetheryte. False when Lifestream wouldn't.</summary>
    public bool Teleport(uint aetheryte) => guard.Call("Teleport", () => teleport.InvokeFunc(aetheryte, 0), false);

    /// <summary>The city's aethernet to a shard, by its name (standing at the aetheryte). False when Lifestream wouldn't.</summary>
    public bool Aethernet(string destination) => guard.Call("AethernetTeleport", () => aethernet.InvokeFunc(destination), false) || Command(destination);

    /// <summary>A world visit on your data centre (or back to your home world).</summary>
    public bool ChangeWorld(string world) => guard.Call("ChangeWorld", () => changeWorld.InvokeFunc(world), false) || Command(world);

    /// <summary>Whether a world can be visited from here (null when Lifestream doesn't say).</summary>
    public bool? CanVisit(string world) => guard.Call<bool?>("CanVisitSameDC", () => canVisitSameDc.InvokeFunc(world), null);

    /// <summary>
    /// To a ward of a residential district: the city's aetheryte, then its Residential District Aethernet.
    /// <paramref name="cityAetheryte"/> is the district's city's aetheryte row (Lifestream's ResidentialAetheryteKind).
    /// </summary>
    public bool GoToWard(uint world, uint cityAetheryte, int ward = 1, int plot = 1) =>
        guard.Do("GoToHousingAddress", () => goToAddress.InvokeAction((string.Empty, (int)world, (int)cityAetheryte, ward, 0, plot, 1, false, false, string.Empty)));

    /// <summary>"/li" and the text, as typed. False when it couldn't be sent.</summary>
    public bool Command(string text) => guard.Do("ExecuteCommand", () => executeCommand.InvokeAction(text));

    public (int Answered, int Asked) Probe()
    {
        guard.Reset();
        return (guard.Call<bool?>("IsBusy", () => isBusy.InvokeFunc(), null) != null ? 1 : 0, 1);
    }
}
