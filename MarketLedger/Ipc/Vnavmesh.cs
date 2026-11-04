using System.Numerics;
using MarketLedger.Core;
using Dalamud.Plugin.Ipc;

namespace MarketLedger.Ipc;

/// <summary>
/// vnavmesh (awgil/ffxiv_navmesh), through its IPC: walking to a spot in the zone you're in (Housing
/// Studio's plugin's wrapper). Every call is wrapped (<see cref="IpcGuard"/>). Framework thread.
/// </summary>
/// <remarks>
/// Verified in vnavmesh's source (<c>vnavmesh/IPCProvider.cs</c>, commit 6fc8072, 31 Aug 2026; each name
/// prefixed "vnavmesh."): <c>Nav.IsReady</c> → bool (line 17), <c>Nav.PathfindCancelAll</c> (line 25; it
/// reloads the mesh, so it's only called while a path is being worked out),
/// <c>SimpleMove.PathfindAndMoveTo</c> (Vector3, bool fly) → bool (line 53),
/// <c>SimpleMove.PathfindInProgress</c> → bool (line 55), <c>Path.IsRunning</c> → bool (line 43),
/// <c>Path.Stop</c> (line 42).
/// </remarks>
public sealed class Vnavmesh
{
    public const string InternalName = "vnavmesh";

    private readonly IpcGuard guard = Plugins.Guard(HelperNames.Vnavmesh);
    private readonly ICallGateSubscriber<bool> isReady;
    private readonly ICallGateSubscriber<Vector3, bool, bool> pathfindAndMoveTo;
    private readonly ICallGateSubscriber<bool> pathfindInProgress;
    private readonly ICallGateSubscriber<bool> isRunning;
    private readonly ICallGateSubscriber<object> stop;
    private readonly ICallGateSubscriber<object> cancelPathfinds;

    public Vnavmesh()
    {
        var pi = Services.PluginInterface;
        isReady = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        pathfindAndMoveTo = pi.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        pathfindInProgress = pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        isRunning = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        stop = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        cancelPathfinds = pi.GetIpcSubscriber<object>("vnavmesh.Nav.PathfindCancelAll");
    }

    public static string Calls => "Nav.IsReady · SimpleMove.PathfindAndMoveTo · SimpleMove.PathfindInProgress · Path.IsRunning · Path.Stop";

    public bool Installed => Plugins.Loaded(InternalName);

    public string? Version => Plugins.Version(InternalName);

    /// <summary>The mesh for this zone is built.</summary>
    public bool Ready => guard.Call("Nav.IsReady", () => isReady.InvokeFunc(), false);

    /// <summary>Starts walking to a spot. False when it couldn't start.</summary>
    public bool MoveTo(Vector3 to) => guard.Call("SimpleMove.PathfindAndMoveTo", () => pathfindAndMoveTo.InvokeFunc(to, false), false);

    private bool Pathfinding => guard.Call("SimpleMove.PathfindInProgress", () => pathfindInProgress.InvokeFunc(), false);

    /// <summary>Finding a path or walking one.</summary>
    public bool Busy => Pathfinding || guard.Call("Path.IsRunning", () => isRunning.InvokeFunc(), false);

    /// <summary>Stops walking (safe to call whenever: nothing happens when it isn't).</summary>
    public void Stop()
    {
        // A path still being worked out starts walking once it's found: cancelled too.
        if (Pathfinding) guard.Do("Nav.PathfindCancelAll", () => cancelPathfinds.InvokeAction());
        guard.Do("Path.Stop", () => stop.InvokeAction());
    }

    public (int Answered, int Asked) Probe()
    {
        guard.Reset();
        var answered = 0;
        if (guard.Call<bool?>("Nav.IsReady", () => isReady.InvokeFunc(), null) != null) answered++;
        if (guard.Call<bool?>("Path.IsRunning", () => isRunning.InvokeFunc(), null) != null) answered++;
        return (answered, 2);
    }
}
