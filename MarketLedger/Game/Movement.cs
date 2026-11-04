using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace MarketLedger.Game;

/// <summary>
/// Getting about faster on a run: the mount roulette where the zone lets you ride (the
/// TerritoryType row's Mount), Sprint where it doesn't, and getting down before using something.
/// General actions by their GeneralAction rows: Sprint 4, Mount Roulette 9, Dismount 23 (each used
/// only when the game says it can be, and its name from the game's sheet is logged the first time,
/// so a wrong row shows in the log). Framework thread.
/// </summary>
public static unsafe class Movement
{
    public const uint Sprint = 4;
    public const uint MountRoulette = 9;
    public const uint Dismount = 23;

    /// <summary>Worth getting on a mount for: shorter walks go on foot (the mount's cast and getting down take a couple of seconds).</summary>
    public const float RideFrom = 35f;

    private static readonly HashSet<uint> named = new();

    /// <summary>On a mount now.</summary>
    public static bool Mounted => Services.Condition[ConditionFlag.Mounted];

    /// <summary>On your feet: not mounted, not getting on or off, not mid-jump (the game uses nothing then).</summary>
    public static bool Landed => !Mounted && !Services.Condition[ConditionFlag.Jumping] && !Services.Condition[ConditionFlag.Jumping61]
                                 && !Services.Condition[ConditionFlag.Mounting] && !Services.Condition[ConditionFlag.Mounting71];

    /// <summary>The zone you're in lets you ride (not a city's streets, not inside a house), and you're not fighting.</summary>
    public static bool CanRideHere
    {
        get
        {
            if (Services.Condition[ConditionFlag.InCombat]) return false;
            return Services.Data.GetExcelSheet<TerritoryType>().TryGetRow(Services.ClientState.TerritoryType, out var t) && t.Mount;
        }
    }

    /// <summary>The game would let this general action be used now (not on cooldown, allowed here).</summary>
    public static bool Ready(uint action)
    {
        var am = ActionManager.Instance();
        return am != null && am->GetActionStatus(ActionType.GeneralAction, action) == 0;
    }

    /// <summary>Uses a general action if the game says it can be; true when it was.</summary>
    public static bool Use(uint action, Action<string> say)
    {
        if (!Ready(action)) return false;
        var am = ActionManager.Instance();
        if (am == null) return false;
        if (named.Add(action))
        {
            var name = Services.Data.GetExcelSheet<GeneralAction>().TryGetRow(action, out var row) ? row.Name.ExtractText() : "?";
            say($"  (General action {action} is \"{name}\".)");
        }
        return am->UseAction(ActionType.GeneralAction, action);
    }

    /// <summary>
    /// Before a walk of <paramref name="distance"/> yalms: on a mount if it's worth it and the zone lets
    /// you (true: wait for <see cref="Mounted"/>), else Sprint when it's ready. Already mounted: nothing.
    /// </summary>
    public static bool SpeedUp(float distance, bool ride, Action<string> say)
    {
        if (Mounted) return false;
        if (ride && distance >= RideFrom && CanRideHere && Use(MountRoulette, say))
        {
            say($"  Mounting up ({distance:0} yalms to go).");
            return true;
        }
        TrySprint(say);
        return false;
    }

    /// <summary>Sprint, when on foot and it's ready (again, once it's off cooldown).</summary>
    public static void TrySprint(Action<string> say)
    {
        if (Mounted) return;
        if (Use(Sprint, say)) say("  Sprinting.");
    }

    /// <summary>Gets down from the mount (before talking to someone or using something); true when it asked to.</summary>
    public static bool GetDown(Action<string> say) => Mounted && Use(Dismount, say);
}
