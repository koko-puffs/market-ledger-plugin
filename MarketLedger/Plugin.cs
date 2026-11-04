using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MarketLedger.Session;
using MarketLedger.UI;

namespace MarketLedger;

/// <summary>
/// Market Ledger: carries out the runs planned on kokos-tools.com (Market Ledger) by asking the plugins
/// you already use to do the work, and tells the site what you hold and what your retainers sell.
/// /ledger opens the window; /ledger stop stops the run in hand; /ledger pause pauses it (and
/// /ledger resume carries on); /ledger settings opens Settings.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/ledger";
    private const string LongCommand = "/marketledger";

    private readonly Configuration config;
    private readonly LedgerSession session;
    private readonly WindowSystem windows = new("MarketLedger");
    private readonly MainWindow main;
    private readonly HudWindow hud;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Services>();
        config = Services.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        try
        {
            session = new LedgerSession(config);
            main = new MainWindow(session, config);
            hud = new HudWindow(session, config);
            hud.OpenMain = () => main.ShowTab(MainTab.Run);
            windows.AddWindow(main);
            windows.AddWindow(hud);

            var help = new CommandInfo(OnCommand)
            {
                HelpMessage = "Open Market Ledger. /ledger stop stops the run in hand, /ledger pause pauses it and /ledger resume carries on; /ledger settings opens Settings.",
            };
            Services.Commands.AddHandler(Command, help);
            Services.Commands.AddHandler(LongCommand, new CommandInfo(OnCommand) { HelpMessage = "The same as /ledger.", ShowInHelp = false });
            Services.PluginInterface.UiBuilder.Draw += windows.Draw;
            Services.PluginInterface.UiBuilder.OpenMainUi += main.ShowMain;
            Services.PluginInterface.UiBuilder.OpenConfigUi += main.ShowSettings;
            Services.Framework.Update += OnUpdate;
        }
        catch
        {
            // Dalamud doesn't dispose a plugin whose constructor threw: what was hooked into the game is let go of here.
            Dispose();
            throw;
        }
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "stop":
                session.Stop();
                break;
            case "pause":
                session.Pause();
                break;
            case "resume":
            case "continue":
                session.Resume();
                break;
            case "settings":
            case "config":
                main.ShowSettings();
                break;
            default:
                main.Toggle();
                break;
        }
    }

    private void OnUpdate(IFramework framework)
    {
        try
        {
            session.Tick();
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Market Ledger's update failed");
        }
    }

    public void Dispose()
    {
        Safely(() => Services.Framework.Update -= OnUpdate);
        Safely(() => Services.PluginInterface.UiBuilder.Draw -= windows.Draw);
        if (main != null)
        {
            Safely(() => Services.PluginInterface.UiBuilder.OpenMainUi -= main.ShowMain);
            Safely(() => Services.PluginInterface.UiBuilder.OpenConfigUi -= main.ShowSettings);
        }
        Safely(() => Services.Commands.RemoveHandler(Command));
        Safely(() => Services.Commands.RemoveHandler(LongCommand));
        Safely(windows.RemoveAllWindows);
        Safely(() => main?.Dispose());
        Safely(() => hud?.Dispose());
        // A run in hand stops here: walking, travel, gathering and crafting, and AutoRetainer is let be.
        Safely(() => session?.Dispose());
    }

    private static void Safely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "Market Ledger couldn't let go of something while unloading");
        }
    }
}
