using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using MarketLedger.Core;
using MarketLedger.Session;
using static MarketLedger.UI.Theme;

namespace MarketLedger.UI;

/// <summary>The main window's tabs.</summary>
public enum MainTab
{
    Home,
    Run,
    Helpers,
    Settings,
}

/// <summary>
/// The plugin's window, in the look of Koko's plugins and Market Ledger's gold. Four tabs: Home (the
/// link, the run in hand with its buttons, the runs sent from the site), Run (the steps, the log and
/// what it's doing now), Helpers (the plugins it drives and how each answers) and Settings (the
/// account and how a run behaves). Everything it shows is read from <see cref="LedgerSession"/>
/// each frame; what you press goes back to it after the frame's drawing is done.
/// </summary>
public sealed partial class MainWindow : Window, IDisposable
{
    private readonly LedgerSession session;
    private readonly Configuration config;
    private readonly Fonts fonts;
    private readonly List<Action> later = new();
    private MainTab tab;
    private bool disposed;

    public MainWindow(LedgerSession session, Configuration config)
        : base("Market Ledger###MarketLedgerMain")
    {
        this.session = session;
        this.config = config;
        fonts = Ui.AcquireFonts();

        Size = new Vector2(900, 660);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(520, 480), MaximumSize = new Vector2(1800, 2400) };
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
    }

    /// <summary>Opens the window on Home.</summary>
    public void ShowMain() => ShowTab(MainTab.Home);

    /// <summary>Opens Settings (Dalamud's cog).</summary>
    public void ShowSettings() => ShowTab(MainTab.Settings);

    /// <summary>Opens a tab (the HUD's open button opens Run).</summary>
    public void ShowTab(MainTab which)
    {
        Dismiss();
        tab = which;
        IsOpen = true;
    }

    public override void OnClose() => Dismiss();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Dismiss();
        Ui.ReleaseFonts();
    }

    private bool firstSizeFitted;

    public override void PreDraw()
    {
        if (!firstSizeFitted)
        {
            // The first size could be taller than the screen.
            firstSizeFitted = true;
            var screenH = ImGui.GetMainViewport().Size.Y;
            var scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
            if (screenH > 0 && scale > 0 && Size is { } size && size.Y * scale > screenH * 0.9f)
                Size = new Vector2(size.X, MathF.Max(480, screenH * 0.9f / scale));
        }
        Theme.PushFrame();
    }

    public override void PostDraw() => Theme.Pop();

    public override void Draw()
    {
        Kit.ResetFrame();
        Frame.ResetFrame();
        Theme.PushInside();
        try
        {
            using (fonts.Push(Face.Sans, 14))
            {
                Kit.Blocked = confirm != null;
                try
                {
                    DrawHeader();
                    switch (tab)
                    {
                        case MainTab.Run:
                            DrawRunTab();
                            break;
                        case MainTab.Helpers:
                            DrawHelpersTab();
                            break;
                        case MainTab.Settings:
                            DrawSettingsTab();
                            break;
                        default:
                            DrawHomeTab();
                            break;
                    }
                }
                finally
                {
                    Frame.Unwind();
                    Frame.DrawToast();
                    Kit.Blocked = false;
                    DrawConfirm();
                }
            }
        }
        finally
        {
            Theme.Pop();
        }

        if (later.Count > 0)
        {
            var actions = later.ToArray();
            later.Clear();
            foreach (var a in actions)
            {
                try
                {
                    a();
                }
                catch (Exception ex)
                {
                    Services.Log.Error(ex, "Market Ledger: an action from the window failed");
                    Notify("Something went wrong (see /xllog)");
                }
            }
        }
    }

    /// <summary>Runs after this frame's drawing is finished (what you press changes the session between frames, not during one).</summary>
    private void Later(Action action) => later.Add(action);

    private static void Notify(string text) => Frame.Toast(text);

    // ---------------------------------------------------------------------------------------------
    // The header: the coins, the name, the link on the right, the tabs under them.

    private readonly TabSpec[] tabs = { new("Home"), new("Run"), new("Helpers"), new("Settings") };
    private int tabsInbox = -1;
    private int tabsRunStep = -2;
    private int tabsHelpers = -1;

    /// <summary>The tabs' counts, made again only when they change.</summary>
    private void UpdateTabs()
    {
        var inbox = session.Inbox.Count;
        if (inbox != tabsInbox)
        {
            tabsInbox = inbox;
            tabs[0] = new TabSpec("Home", inbox > 0 ? Ui.Count(inbox) : null);
        }
        var run = session.Current;
        var step = run != null && Ui.Going(run.Status) ? run.StepIndex : -1;
        var stepKey = run == null ? -1 : step * 1000 + run.Steps.Count;
        if (stepKey != tabsRunStep)
        {
            tabsRunStep = stepKey;
            tabs[1] = new TabSpec("Run", run != null && step >= 0 ? $"{step + 1}/{run.Steps.Count}" : null);
        }
        var helpers = session.Helpers;
        var missing = 0;
        for (var i = 0; i < helpers.Count; i++)
            if (helpers[i].Needed && (!helpers[i].Found || helpers[i].Ipc == HelperIpc.No)) missing++;
        if (missing != tabsHelpers)
        {
            tabsHelpers = missing;
            tabs[2] = new TabSpec("Helpers", missing > 0 ? $"{missing} missing" : null);
        }
    }

    private void DrawHeader()
    {
        Frame.BeginHeader(16);
        var at = Kit.At;
        var w = Kit.Width;
        var dl = Kit.Dl;
        Kit.IconWell(at, S(36), Icon.Coins);
        var tx = at.X + S(36 + 12);
        var (linkText, linkColor) = LinkWords();
        var linkW = Txt.Measure(linkText, Face.Sans, 12).X + S(14);
        var titleW = w - (tx - at.X) - linkW - S(12);
        Txt.Line(dl, new Vector2(tx, at.Y), Txt.Fit("Market Ledger", Face.SansSemibold, 18, titleW), Face.SansSemibold, 18, Palette.Ink, 36);
        var lx = at.X + w - linkW;
        Kit.Dot(new Vector2(lx + S(3.5f), at.Y + S(18)), linkColor);
        Txt.Line(dl, new Vector2(lx + S(14), at.Y), linkText, Face.Sans, 12, session.Link is LinkState.Linked ? Palette.Gain : Palette.Muted, 36);
        Kit.Reserve(at, w, S(36));
        Kit.Gap(14);
        UpdateTabs();
        var picked = Kit.Tabs("##ml-tabs", tabs, (int)tab);
        if (picked != (int)tab)
        {
            tab = (MainTab)picked;
            Dismiss();
        }
        Frame.EndHeader();
    }

    /// <summary>The link on the header's right: "kokos-tools.com" in green when linked.</summary>
    private (string Text, Vector4 Dot) LinkWords() => session.Link switch
    {
        LinkState.Linked => ("kokos-tools.com", Palette.Gain),
        LinkState.Linking => ("Linking…", Palette.Gold),
        LinkState.Refused => ("Link refused", Palette.Loss),
        _ => ("Not linked", Palette.Faint),
    };

    // ---------------------------------------------------------------------------------------------
    // Titles.

    /// <summary>
    /// A title in Geist 600 on up to <paramref name="lines"/> lines, cut with an ellipsis on the last.
    /// Returns its height; with <paramref name="draw"/> false, only measures.
    /// </summary>
    private static float Title(Vector2 pos, string text, float maxWidth, int px = 18, int lines = 2, bool draw = true, Vector4? color = null)
    {
        var lineCss = px * 1.2f;
        var lh = S(lineCss);
        maxWidth = MathF.Max(S(40), maxWidth);
        var rows = Lines.Of(text, maxWidth, Face.SansSemibold, px);
        var shown = Math.Min(rows.Length, lines);
        if (!draw) return lh * shown;
        var dl = Kit.Dl;
        for (var i = 0; i < shown; i++)
        {
            var row = rows[i];
            // The last line shown carries an ellipsis when there's more.
            if (i == shown - 1 && rows.Length > shown) row = Txt.Fit(row + " …", Face.SansSemibold, px, maxWidth);
            Txt.Line(dl, pos + new Vector2(0, lh * i), row, Face.SansSemibold, px, color ?? Palette.Ink, lineCss);
        }
        return lh * shown;
    }

    // ---------------------------------------------------------------------------------------------
    // Rows of settings and facts.

    /// <summary>
    /// A setting's row (13px 16px inside, a hairline between): its name and a line under it, its
    /// control on the right (or under the words, <paramref name="below"/>). Returns where the
    /// control goes: draw it there after (the row's room is already taken).
    /// </summary>
    private static Vector2 SetRow(int index, string name, string? note, float controlW, float controlH, bool below = false, string? tip = null,
        bool disabled = false, Icon? icon = null, Vector4? noteColor = null)
    {
        var at = Kit.At;
        var w = Kit.Width;
        var dl = Kit.Dl;
        if (index > 0) dl.AddLine(new Vector2(at.X + 1, at.Y - 0.5f), new Vector2(at.X + w - 1, at.Y - 0.5f), Palette.U32(Palette.Line), 1f);
        var x = at.X + S(16);
        var wordsW = below || controlW <= 0 ? w - S(32) : w - S(32) - controlW - S(14);
        var nameH = Txt.Natural(Face.SansMedium, 14);
        var noteH = note != null ? Lines.Height(note, wordsW, Face.Sans, 12, 18) : 0;
        var wordsH = nameH + (note != null ? S(2) + noteH : 0);
        var h = below ? S(13) * 2 + wordsH + (controlH > 0 ? S(10) + controlH : 0) : S(13) * 2 + MathF.Max(wordsH, controlH);
        var top = below ? at.Y + S(13) : at.Y + (h - wordsH) / 2;
        var old = Palette.Opacity;
        if (disabled) Palette.Opacity = old * 0.5f;
        var nx = x;
        if (icon is { } ic)
        {
            Icons.Draw(dl, ic, new Vector2(nx, top + (nameH - S(14)) / 2), S(14), Palette.Gold);
            nx += S(14 + 6);
        }
        Txt.Line(dl, new Vector2(nx, top), Txt.Fit(name, Face.SansMedium, 14, x + wordsW - nx), Face.SansMedium, 14, Palette.Ink);
        if (note != null) Lines.Draw(dl, new Vector2(x, top + nameH + S(2)), wordsW, note, Face.Sans, 12, noteColor ?? Palette.Muted, 18);
        Palette.Opacity = old;
        if (tip != null && !Kit.Blocked && ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(new Vector2(x, top), new Vector2(x + wordsW, top + wordsH)))
            Kit.Tooltip(tip);
        Kit.Reserve(at, w, h);
        return below ? new Vector2(x, top + wordsH + S(10)) : new Vector2(at.X + w - S(16) - controlW, at.Y + (h - controlH) / 2);
    }

    /// <summary>A setting with a switch; true when it's flipped.</summary>
    private static bool SwitchRow(int index, string id, string name, string? note, bool on, string? tip = null, bool disabled = false)
    {
        var p = SetRow(index, name, note, S(44), S(24), false, tip, disabled);
        return Kit.Switch(id, p, on, disabled);
    }

    /// <summary>A setting with a − value + stepper; returns −1, 0 or +1.</summary>
    private static int StepperRow(int index, string id, string name, string? note, string value, bool canDown, bool canUp, string? tip = null)
    {
        var p = SetRow(index, name, note, Kit.StepperWidth(value), S(36), false, tip);
        return Kit.Stepper(id, p, value, canDown, canUp);
    }

    /// <summary>A setting with a small button on the right; true when it's pressed.</summary>
    private static bool ButtonRow(int index, string id, string name, string? note, string label, Btn style = Btn.Glass, bool disabled = false,
        string? buttonTip = null, string? tip = null, Icon? buttonIcon = null)
    {
        var p = SetRow(index, name, note, Kit.ButtonWidth(label, BtnSize.Tiny, buttonIcon, style), Kit.ButtonHeight(BtnSize.Tiny), false, tip);
        return Kit.Button(id, p, label, style, BtnSize.Tiny, buttonIcon, 0, disabled, buttonTip);
    }

    /// <summary>A label on the left and a number on the right in mono (the Now box's rows).</summary>
    private static void NumberRow(string label, string value, Vector4? color = null)
    {
        var at = Kit.At;
        var w = Kit.Width;
        var h = S(22);
        var dl = Kit.Dl;
        Txt.Line(dl, at, label, Face.Sans, 12, Palette.Muted, 22);
        var vw = Txt.Measure(value, Face.Mono, 12).X;
        Txt.Line(dl, new Vector2(at.X + w - vw, at.Y), value, Face.Mono, 12, color ?? Palette.Ink, 22);
        Kit.Reserve(at, w, h);
    }

    // ---------------------------------------------------------------------------------------------
    // The question: a card over the window, its answers saying what happens.

    private sealed record Confirmation(string Title, string Body, string Yes, string No, Action OnYes, bool Danger);

    private Confirmation? confirm;
    private float confirmCardH;
    private bool confirmFocused;

    private void Ask(string title, string body, string yes, string no, Action onYes, bool danger = false)
    {
        confirm = new Confirmation(title, body, yes, no, onYes, danger);
        confirmFocused = false;
    }

    /// <summary>The question goes unanswered: the window closed, or another tab opened.</summary>
    private void Dismiss()
    {
        confirm = null;
        confirmCardH = 0;
    }

    /// <summary>
    /// The question over the window: a window of its own the size of this one, kept in front, which
    /// darkens only this window and takes every click on it; the card in its middle.
    /// </summary>
    private void DrawConfirm()
    {
        if (confirm == null) return;
        var c = confirm;
        var winPos = ImGui.GetWindowPos();
        var winSize = ImGui.GetWindowSize();
        ImGui.SetNextWindowPos(winPos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(winSize, ImGuiCond.Always);
        if (!confirmFocused || ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
        {
            ImGui.SetNextWindowFocus();
            confirmFocused = true;
        }
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, S(14));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Vector4.Zero);
        var shown = ImGui.Begin("##ml-confirm-over", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoFocusOnAppearing);
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);
        try
        {
            if (!shown) return;
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(winPos, winPos + winSize, Palette.U32(Palette.WithAlpha(Palette.Ground, 0.75f)), S(14));
            var width = MathF.Min(S(420), winSize.X - S(48));
            var pad = S(24);
            var inner = width - pad * 2;
            var cardH = confirmCardH > 0 ? confirmCardH : S(200);
            var cardPos = new Vector2(winPos.X + (winSize.X - width) / 2, winPos.Y + MathF.Max(S(40), (winSize.Y - cardH) / 2));
            var r = S(Palette.RadiusXl);
            dl.AddRectFilled(cardPos, cardPos + new Vector2(width, cardH), Palette.U32(Palette.Card), r);
            dl.AddRect(cardPos, cardPos + new Vector2(width, cardH), Palette.U32(Palette.Line), r);

            var at = cardPos + new Vector2(pad, pad);
            ImGui.SetCursorScreenPos(at);
            Kit.PushBox(at.X, inner);
            var titleH = Title(at, c.Title, inner, 18, 2);
            Kit.Reserve(at, inner, titleH);
            Kit.Gap(8);
            Lines.Para(c.Body, Palette.Soft, 14, 21);
            Kit.Gap(20);
            var buttons = Kit.At;
            var yesStyle = c.Danger ? Btn.Danger : Btn.Primary;
            var yesW = Kit.ButtonWidth(c.Yes, BtnSize.Md, null, yesStyle);
            var noW = Kit.ButtonWidth(c.No);
            var close = false;
            if (Kit.Button("##confirm-no", new Vector2(buttons.X + inner - yesW - S(8) - noW, buttons.Y), c.No)) close = true;
            if (Kit.Button("##confirm-yes", new Vector2(buttons.X + inner - yesW, buttons.Y), c.Yes, yesStyle))
            {
                close = true;
                Later(c.OnYes);
            }
            if (ImGui.IsKeyPressed(ImGuiKey.Escape)) close = true;
            Kit.Reserve(buttons, inner, Kit.ButtonHeight(BtnSize.Md));
            Kit.PopBox();
            confirmCardH = Kit.At.Y - cardPos.Y + pad;
            if (close && ReferenceEquals(confirm, c)) confirm = null;
        }
        finally
        {
            ImGui.End();
        }
    }
}
