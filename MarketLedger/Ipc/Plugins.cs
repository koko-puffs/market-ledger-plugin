using System;
using System.Collections.Generic;
using System.Linq;
using MarketLedger.Core;

namespace MarketLedger.Ipc;

/// <summary>
/// Whether another plugin is installed and loaded, by its internal name, from Dalamud's list, and its
/// version. Checked before any of its IPC is called, so a missing plugin is told apart from one that
/// failed. The list is read at most every 2 seconds (the window asks every frame).
/// </summary>
public static class Plugins
{
    private static readonly Dictionary<string, (DateTime Until, bool Loaded, string? Version)> Known = new(StringComparer.OrdinalIgnoreCase);

    public static bool Loaded(string internalName) => Read(internalName).Loaded;

    /// <summary>"4.0.5.21", or null when it isn't installed.</summary>
    public static string? Version(string internalName) => Read(internalName).Version;

    private static (bool Loaded, string? Version) Read(string internalName)
    {
        var now = DateTime.UtcNow;
        if (Known.TryGetValue(internalName, out var k) && now < k.Until) return (k.Loaded, k.Version);
        bool loaded;
        string? version = null;
        try
        {
            var p = Services.PluginInterface.InstalledPlugins.FirstOrDefault(p => p.IsLoaded && string.Equals(p.InternalName, internalName, StringComparison.OrdinalIgnoreCase));
            loaded = p != null;
            version = p?.Version.ToString();
        }
        catch (Exception)
        {
            loaded = false;
        }
        Known[internalName] = (now + TimeSpan.FromSeconds(2), loaded, version);
        return (loaded, version);
    }

    /// <summary>A guard for one plugin's calls, logging each call's first failure (at debug: a missing plugin is normal).</summary>
    public static IpcGuard Guard(string plugin) => new(plugin, (ex, what) => Services.Log.Debug(ex, what));
}
