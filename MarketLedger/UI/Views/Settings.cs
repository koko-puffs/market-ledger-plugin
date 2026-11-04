using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using MarketLedger.Core;
using MarketLedger.Session;
using static MarketLedger.UI.Theme;

namespace MarketLedger.UI;

/// <summary>
/// Settings, as Koko's plugins lay theirs: boxes of rows, each a setting's name, a line under it
/// and its control on the right (the longer words are its tooltip). Your account (paste a token,
/// Link, Unlink, get one on the site), how a run behaves while it runs, and the HUD. The version
/// and What's new at the foot.
/// </summary>
public sealed partial class MainWindow
{
    private const string ReleasesUrl = "https://github.com/koko-puffs/market-ledger-plugin/releases";

    /// <summary>The steps of the market ceiling, in gil for a whole run (0: only the gil agreed for the run).</summary>
    private static readonly int[] Ceilings =
    {
        0, 10_000, 25_000, 50_000, 100_000, 250_000, 500_000, 1_000_000, 2_500_000, 5_000_000, 10_000_000, 25_000_000, 50_000_000, 100_000_000,
    };

    private string tokenDraft = string.Empty;
    private string? tokenProblem;
    private readonly Memo<int> slotsValue = new();
    private readonly Memo<int> ceilingValue = new();
    private string? maskedFor;
    private string masked = string.Empty;

    private void DrawSettingsTab()
    {
        Frame.BeginBody("##settings", null, 18, 22);

        Kit.Section("Your account");
        Kit.Gap(10);
        Kit.BeginPanel("##set-account", 0, 0, 0, Palette.Card, Palette.Line, Palette.RadiusMd);
        DrawAccountRow(0);
        if (ButtonRow(1, "##set-site", "Market Ledger on the site", "Plan runs, send them here, keep the books", "Open", Btn.Glass, false, null,
                "kokos-tools.com/market-ledger", Icon.ExternalLink))
            Dalamud.Utility.Util.OpenLink(Ui.SiteUrl);
        Kit.EndPanel();
        Kit.Gap(10);
        Lines.Para("Anyone with the token can send runs to this plugin and read what it reports, so keep it to yourself. Revoke it on the site if it gets out.", Palette.Faint, 12, 18);

        Kit.Gap(24);
        Kit.Section("While running");
        Kit.Gap(10);
        Kit.BeginPanel("##set-running", 0, 0, 0, Palette.Card, Palette.Line, Palette.RadiusMd);
        var i = 0;
        if (SwitchRow(i++, "##set-tell", "Pause on a tell", "A whisper pauses the run until you resume it", config.PauseOnTell,
                "When someone sends you a tell, the run pauses where it is (safely, between actions) so you can answer. Resume from here, the HUD or the site."))
        {
            config.PauseOnTell = !config.PauseOnTell;
            config.Save();
        }
        var slots = slotsValue.Get(config.KeepFreeSlots, static n => n == 0 ? "Off" : Ui.Count(n));
        var delta = StepperRow(i++, "##set-slots", "Keep free bag slots", "Pauses when fewer are free (crafts and gathering fill bags)", slots,
            config.KeepFreeSlots > 0, config.KeepFreeSlots < 60,
            "When your bags have fewer free slots than this, the run pauses before the next action, so nothing is lost to full bags. Off never pauses for space.");
        if (delta != 0)
        {
            config.KeepFreeSlots = Math.Clamp(config.KeepFreeSlots + delta, 0, 60);
            config.Save();
        }
        var step = CeilingStep(config.MarketCeiling);
        var ceiling = ceilingValue.Get(config.MarketCeiling, static g => g <= 0 ? "Off" : Ui.Gil(g));
        var cd = StepperRow(i++, "##set-ceiling", "Market ceiling", "The most the market may spend in one run, whatever was agreed", ceiling,
            step > 0, step < Ceilings.Length - 1,
            "Each line of a run has its own most it may pay for one (the site's price plus a little), and a run never spends past the gil you agreed to when you sent it. This is a ceiling of your own on top: no run spends more than this on the market board, all together. Off leaves it to the run.");
        if (cd != 0)
        {
            config.MarketCeiling = Ceilings[Math.Clamp(step + cd, 0, Ceilings.Length - 1)];
            config.Save();
        }
        if (SwitchRow(i++, "##set-trust", "Trust the new clicks", config.TrustClicks ? "Clicks not yet seen working go ahead by themselves" : "The run holds before each until you press Continue",
                config.TrustClicks,
                "Some of the game's windows Market Ledger clicks through haven't been seen working yet (a retainer's item menu, entrusting). Until you trust them, the run holds before each and waits for Continue (in the HUD or the window), so you can watch it once. Buying stays at or under each line's price and never past the agreed gil either way."))
        {
            config.TrustClicks = !config.TrustClicks;
            config.Save();
        }
        if (SwitchRow(i++, "##set-site-control", "Let the site pause and stop", "The run's page can pause, resume, skip and stop it", config.LetSiteControl,
                "The buttons on the run's page on kokos-tools.com (on your phone, say) reach the game. Off, only this window and the HUD do."))
        {
            config.LetSiteControl = !config.LetSiteControl;
            config.Save();
        }
        if (SwitchRow(i++, "##set-chat", "Say it in chat", "Pauses, stops and a run's end", config.ChatMessages,
                "A line in your chat when a run pauses, stops, waits for you or ends, so you notice with the window closed."))
        {
            config.ChatMessages = !config.ChatMessages;
            config.Save();
        }
        if (SwitchRow(i++, "##set-ar", "Hold AutoRetainer", "Its own runs wait while a run uses your retainers or the market", config.HoldAutoRetainer,
                "AutoRetainer sends retainers out and collects them by itself; while a run takes from or entrusts to them, or buys, it's held so the two don't fight over the same windows. It carries on when the run ends."))
        {
            config.HoldAutoRetainer = !config.HoldAutoRetainer;
            config.Save();
        }
        Kit.EndPanel();

        Kit.Gap(24);
        Kit.Section("On screen");
        Kit.Gap(10);
        Kit.BeginPanel("##set-screen", 0, 0, 0, Palette.Card, Palette.Line, Palette.RadiusMd);
        if (SwitchRow(0, "##set-hud", "Show the HUD", "A small box with the step and its buttons while a run is in hand", config.ShowHud,
                "Top right by default (drag it anywhere): the step, a strip of the steps, the time, Pause, and Continue when a click waits for you. A run sent from the site asks there whether to start it."))
        {
            config.ShowHud = !config.ShowHud;
            config.Save();
        }
        Kit.EndPanel();

        Frame.BeginBottom("settings-bottom");
        if (Txt.Para(FooterRuns, 19.5f, center: true) == "news")
            Dalamud.Utility.Util.OpenLink(ReleasesUrl);
        Frame.EndBottom();
        Frame.EndBody();
    }

    /// <summary>"Market Ledger 0.1.0 · ", read once (it's a reflection call).</summary>
    private static readonly string VersionLine = typeof(MainWindow).Assembly.GetName().Version is { } v
        ? $"Market Ledger {v.Major}.{v.Minor}.{v.Build} · "
        : "Market Ledger · ";

    private static readonly Run[] FooterRuns =
    {
        new(VersionLine, Face.Sans, 13, Palette.Faint),
        new("What's new", Face.SansMedium, 13, Palette.Light, Link: "news"),
    };

    private static int CeilingStep(int gil)
    {
        var best = 0;
        for (var i = 0; i < Ceilings.Length; i++)
            if (Ceilings[i] <= gil) best = i;
        return best;
    }

    // ---------------------------------------------------------------------------------------------
    // The account.

    private void DrawAccountRow(int index)
    {
        const string name = "kokos-tools.com account";
        var link = session.Link;
        if (link is LinkState.Linked or LinkState.Linking)
        {
            const string unlink = "Unlink";
            var note = AccountNote(link is LinkState.Linked l ? l.Account : null);
            var p = SetRow(index, name, note, Kit.ButtonWidth(unlink, BtnSize.Tiny), Kit.ButtonHeight(BtnSize.Tiny), false,
                "Runs you send from Market Ledger on the site come here, and the plugin reports back as they go.", false, Icon.Link);
            if (Kit.Button("##set-unlink", p, unlink, Btn.Glass, BtnSize.Tiny, null, 0, false,
                    "Forgets the token here. Revoke it on the site too if you won't use it again."))
                Ask("Unlink from kokos-tools.com?", "The plugin forgets its token: runs stop arriving and nothing more is reported. A run in hand carries on in the game.",
                    "Unlink", "Keep it", Unlink, danger: true);
            return;
        }
        var width = Kit.Width - S(32);
        const string linkLabel = "Link";
        const string get = "Get a token";
        var linkW = Kit.ButtonWidth(linkLabel, BtnSize.Md, null, Btn.Primary);
        var getW = Kit.ButtonWidth(get, BtnSize.Md, Icon.ExternalLink, Btn.Glass);
        var refused = link is LinkState.Refused r ? r.Why : null;
        var why = tokenProblem ?? refused ?? "Not linked yet. Make a token on the site (Settings → Market Ledger → The plugin) and paste it here.";
        var pos = SetRow(index, name, why, width, S(36), true,
            "Linking lets runs come here from the site, and the plugin report its progress. The token is made on the site, shown once, and can be revoked there.",
            false, Icon.Link, tokenProblem != null || refused != null ? Palette.Loss : null);
        // Narrow, the buttons go under the field.
        var fieldW = MathF.Max(S(160), width - linkW - getW - S(16));
        var stacked = fieldW + linkW + getW + S(16) > width + 0.5f;
        if (stacked) fieldW = width;
        var text = tokenDraft;
        if (Kit.Field("##set-token", pos, fieldW, ref text, "mlp_…", Icon.Link, 128, ImGuiInputTextFlags.Password))
        {
            tokenDraft = text;
            tokenProblem = null;
        }
        var bx = stacked ? pos.X : pos.X + fieldW + S(8);
        var by = stacked ? pos.Y + S(36) + S(8) : pos.Y;
        if (Kit.Button("##set-link", new Vector2(bx, by), linkLabel, Btn.Primary, BtnSize.Md, null, 0, tokenDraft.Trim().Length == 0))
            LinkToken();
        if (Kit.Button("##set-get", new Vector2(bx + linkW + S(8), by), get, Btn.Glass, BtnSize.Md, Icon.ExternalLink))
            Dalamud.Utility.Util.OpenLink(Ui.TokensUrl);
        if (stacked) Kit.Reserve(new Vector2(Kit.Left, pos.Y + S(36)), Kit.Width, S(8) + S(36) + S(13));
    }

    private string? noteAccount;

    /// <summary>"Linked as Koko · mlp_••••Ab3x" (enough to tell tokens apart, never the token), made again only when either changes.</summary>
    private string AccountNote(string? account)
    {
        var token = config.Token ?? string.Empty;
        if (token == maskedFor && account == noteAccount && masked.Length > 0) return masked;
        maskedFor = token;
        noteAccount = account;
        var shown = token.Length > 8 ? $"mlp_••••{token[^4..]}" : "mlp_••••";
        return masked = account != null ? $"Linked as {account} · {shown}" : $"Linking… · {shown}";
    }

    private void LinkToken()
    {
        var token = string.Concat(tokenDraft.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (!token.StartsWith("mlp_", StringComparison.Ordinal) || token.Length != 47)
        {
            tokenProblem = "That isn't a Market Ledger token: it starts mlp_ and is 47 letters long.";
            return;
        }
        tokenDraft = string.Empty;
        tokenProblem = null;
        Later(() =>
        {
            config.Token = token;
            config.Save();
            session.Refresh();
            Notify("Linking to kokos-tools.com…");
        });
    }

    private void Unlink()
    {
        config.Token = string.Empty;
        config.Save();
        session.Refresh();
        Notify("Unlinked from kokos-tools.com");
    }
}
