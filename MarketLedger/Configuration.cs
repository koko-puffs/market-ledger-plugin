using System;
using Dalamud.Configuration;

namespace MarketLedger;

/// <summary>The plugin's settings, kept by Dalamud with the plugin's config.</summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>The token from the site (Settings → Market Ledger → The plugin), "mlp_" and 43 letters. Never logged.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// This install's own salt for characters' keys (<see cref="Core.SiteLink.CharacterKey"/>): made once,
    /// so the site never sees a content id.
    /// </summary>
    public string Salt { get; set; } = string.Empty;

    /// <summary>A run pauses when someone sends you a tell.</summary>
    public bool PauseOnTell { get; set; } = true;

    /// <summary>Bag slots a run leaves free: it pauses rather than fill them (buying, taking from retainers).</summary>
    public int KeepFreeSlots { get; set; } = 5;

    /// <summary>The most the market board may spend in one run, all together, in gil (0: only the gil agreed for the run).</summary>
    public int MarketCeiling { get; set; }

    /// <summary>
    /// Clicks not yet seen working in the game (opening a retainer's item menu, entrusting) go without
    /// holding for you. Off: the run holds before each until you press Resume (Continue).
    /// </summary>
    public bool TrustClicks { get; set; }

    /// <summary>AutoRetainer is held (its "suppressed" state) while a run goes, and let be after.</summary>
    public bool HoldAutoRetainer { get; set; } = true;

    /// <summary>The small window over the game while a run goes.</summary>
    public bool ShowHud { get; set; } = true;

    /// <summary>The site may pause, resume, skip and stop a run in hand (its buttons on the run's page).</summary>
    public bool LetSiteControl { get; set; } = true;

    /// <summary>Pauses, stops and the end of a run said in chat.</summary>
    public bool ChatMessages { get; set; } = true;

    public void Save() => Services.PluginInterface.SavePluginConfig(this);
}
