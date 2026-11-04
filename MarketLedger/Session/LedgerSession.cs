using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Text;
using MarketLedger.Core;
using MarketLedger.Game;
using MarketLedger.Ipc;

namespace MarketLedger.Session;

/// <summary>
/// Everything the windows read and do, in one place: the link to the site, the runs sent from it, the
/// run in hand, the helper plugins, and the character you're playing. Framework thread only (the
/// windows draw on it; <see cref="Tick"/> runs on it).
/// </summary>
public sealed class LedgerSession : IDisposable
{
    private readonly Configuration config;
    private readonly Vendors vendors = new();
    private readonly HoldingsReader holdings = new();
    private readonly AllaganTools allagan = new();
    private readonly RunEngine engine;
    private readonly SiteSync sync;
    private readonly List<Guid> later = new();
    private string lastToken;

    public LedgerSession(Configuration config)
    {
        this.config = config;
        lastToken = config.Token;
        engine = new RunEngine(config, vendors);
        sync = new SiteSync(config, engine, holdings, allagan);
        vendors.WarmUp();
        InGame.WarmUpNames();
        BoardListings.Hook();
        Services.Chat.ChatMessage += OnChat;
        CheckHelpers();
    }

    // ---------------------------------------------------------------------------------------------
    // What the windows read.

    public LinkState Link => sync.Link;

    /// <summary>Runs sent from the site, not started: the oldest first, those you said "later" to after.</summary>
    public IReadOnlyList<WaitingRun> Inbox
    {
        get
        {
            if (inboxFor != (sync.Version, later.Count))
            {
                inboxFor = (sync.Version, later.Count);
                var runs = sync.Waiting.Where(w => !(engine.Active && engine.File?.Guid == w.Id))
                    .Select(w => new WaitingRun(w.Id, w.Name, w.SentAt, w.File, later.Contains(w.Id)));
                inbox = RunOrder.Inbox(runs, w => w.Id, w => w.SentAt, later);
            }
            return inbox;
        }
    }

    private IReadOnlyList<WaitingRun> inbox = Array.Empty<WaitingRun>();
    private (int, int) inboxFor = (-1, -1);

    /// <summary>The run in hand (null if none), made afresh when it changes, and at most four times a second for your bags' counts.</summary>
    public RunState? Current
    {
        get
        {
            if (engine.File is not { } file) return null;
            var now = DateTime.UtcNow;
            if (current != null && currentFor == engine.Version && now - currentAt < TimeSpan.FromMilliseconds(250)) return current;
            currentFor = engine.Version;
            currentAt = now;
            var steps = engine.StepsNow();
            current = new RunState
            {
                File = file,
                Status = engine.Status,
                StepIndex = engine.StepIndex,
                Steps = steps.Select((s, i) =>
                {
                    var step = file.Steps[i];
                    return new StepProgress(s.Id, s.State, s.Done, s.Of, s.Note, StepMath.Label(step, InGame.ItemName), RunWords.Of(step.Kind), StepMath.By(step.Kind));
                }).ToList(),
                Spent = engine.Spent,
                StartedAt = engine.StartedAt,
                Log = engine.Log.Select(l => new LogLine(l.At, l.Who, l.Text)).ToList(),
                Message = engine.Message,
                Doing = engine.Doing,
                Left = engine.Left(),
                HeldForYou = engine.HeldForYou,
                CopyText = engine.CopyText,
            };
            return current;
        }
    }

    private RunState? current;
    private int currentFor = -1;
    private DateTime currentAt;

    public IReadOnlyList<HelperStatus> Helpers { get; private set; } = Array.Empty<HelperStatus>();

    public CharacterSnapshot? Character => sync.Character;

    /// <summary>Why the last Start didn't (null when it did), in words for players.</summary>
    public string? Notice { get; private set; }

    /// <summary>Something the windows show changed.</summary>
    public event Action? Changed;

    // ---------------------------------------------------------------------------------------------
    // What the windows do.

    /// <summary>Starts a run from the inbox; a run in hand is stopped first (the window asks you before).</summary>
    public void Start(Guid runId)
    {
        var waiting = sync.Waiting.FirstOrDefault(w => w.Id == runId);
        if (waiting.File == null)
        {
            Notify("That run isn't in the inbox any more.");
            return;
        }
        if (engine.Active) engine.Stop($"Put aside for \"{waiting.Name}\".");
        if (engine.CanStart(waiting.File) is { } why)
        {
            Notify($"\"{waiting.Name}\" can't start: {why}");
            return;
        }
        Notice = null;
        later.Remove(runId);
        engine.Start(waiting.File);
        sync.RunChanged();
        Changed?.Invoke();
    }

    public void Pause() => engine.Pause("Paused by you.");

    /// <summary>Carries on after a pause (the step starts again), or lets a held click or question go.</summary>
    public void Resume() => engine.Resume();

    public void Stop() => engine.Stop("Stopped by you.");

    public void SkipStep() => engine.Skip();

    /// <summary>Leaves a waiting run for after the others.</summary>
    public void Later(Guid runId)
    {
        later.Remove(runId);
        later.Add(runId);
        Changed?.Invoke();
    }

    /// <summary>Checks each helper again: installed, its version, and whether its IPC answers.</summary>
    public void CheckHelpers()
    {
        var list = new List<HelperStatus>();
        HelperStatus Make(string name, string internalName, bool needed, bool found, string? version, (int Answered, int Asked) probe, string what, string? note, string url, string calls) =>
            new(name, needed, found, version, HelperCheck.Judge(found, probe.Answered, probe.Asked), what, note) { InternalName = internalName, Url = url, Calls = calls };

        var a = engine.Artisan;
        var aFound = a.Installed;
        list.Add(Make(HelperNames.Artisan, Artisan.InternalName, true, aFound, a.Version, aFound ? a.Probe() : (0, 1),
            "Crafts each recipe the set number of times (its Endurance mode); the plugin watches your bags.",
            null, "https://github.com/PunishXIV/Artisan", Artisan.Calls));

        var g = engine.GatherBuddy;
        var gFound = g.Installed;
        var lists = gFound && g.HasLists;
        list.Add(Make(HelperNames.GatherBuddy, GatherBuddy.InternalName, true, gFound, g.PluginVersion, gFound ? g.Probe() : (0, 1),
            "Gathers each run's materials with auto-gather, timed nodes and all; the plugin watches your bags.",
            !gFound ? null
            : lists ? "Its IPC takes lists: each run gets its own auto-gather list (your own lists are turned off meanwhile, and back on after)."
            : "Lists over IPC aren't in GatherBuddy Reborn yet: for each gathering step you import the run's list (Import from TeamCraft), then the plugin turns auto-gather on and watches your bags.",
            "https://github.com/FFXIV-CombatReborn/GatherBuddyReborn", GatherBuddy.Calls));

        var n = engine.Nav;
        var nFound = n.Installed;
        list.Add(Make(HelperNames.Vnavmesh, Vnavmesh.InternalName, true, nFound, n.Version, nFound ? n.Probe() : (0, 1),
            "Walks to vendors, bells and market boards (GatherBuddy Reborn walks with it too).",
            null, "https://github.com/awgil/ffxiv_navmesh", Vnavmesh.Calls));

        var l = engine.Travel;
        var lFound = l.Installed;
        list.Add(Make(HelperNames.Lifestream, Lifestream.InternalName, true, lFound, l.Version, lFound ? l.Probe() : (0, 1),
            "Teleports, the aethernet, and other worlds' market boards (and back home).",
            null, "https://github.com/NightmareXIV/Lifestream", Lifestream.Calls));

        var r = engine.AutoRetainer;
        var rFound = r.Installed;
        list.Add(Make(HelperNames.AutoRetainer, AutoRetainer.InternalName, false, rFound, r.Version, rFound ? r.Probe() : (0, 1),
            "Held while a run goes, so it doesn't open retainers meanwhile; names your retainers.",
            "It has no IPC to take things from retainers or give them: the plugin does that at the bell itself.",
            "https://github.com/PunishXIV/AutoRetainer", AutoRetainer.Calls));

        var t = allagan;
        var tFound = t.Installed;
        list.Add(Make(HelperNames.AllaganTools, AllaganTools.InternalName, false, tFound, t.Version, tFound ? t.Probe() : (0, 1),
            "What your retainers hold, without opening each, so the site plans around it.",
            null, "https://github.com/Critical-Impact/InventoryTools", AllaganTools.Calls));

        Helpers = list;
        helpersAt = DateTime.UtcNow;
        Changed?.Invoke();
    }

    private DateTime helpersAt;

    /// <summary>Reads the settings again: a changed token links afresh (and the inbox is asked at once).</summary>
    public void Refresh()
    {
        if (config.Token != lastToken)
        {
            lastToken = config.Token;
            sync.Relinked();
        }
        else sync.RunChanged();
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------------------------------------
    // Every frame.

    private int engineSeen = -1;
    private int syncSeen = -1;

    public void Tick()
    {
        engine.Tick();
        sync.Tick();
        if (DateTime.UtcNow - helpersAt > TimeSpan.FromSeconds(30) && !engine.Active) CheckHelpers();
        if (engine.Version != engineSeen || sync.Version != syncSeen)
        {
            engineSeen = engine.Version;
            syncSeen = sync.Version;
            Changed?.Invoke();
        }
    }

    private void OnChat(Dalamud.Game.Chat.IHandleableChatMessage message)
    {
        try
        {
            if (message.LogKind == XivChatType.TellIncoming) engine.Tell(message.Sender.TextValue);
        }
        catch (Exception ex)
        {
            Services.Log.Debug(ex, "Market Ledger couldn't read a chat message");
        }
    }

    private void Notify(string words)
    {
        Notice = words;
        Services.Chat.PrintError(words, "Market Ledger");
        Changed?.Invoke();
    }

    public void Dispose()
    {
        Services.Chat.ChatMessage -= OnChat;
        engine.Dispose();
        BoardListings.Unhook();
        sync.Dispose();
    }
}
