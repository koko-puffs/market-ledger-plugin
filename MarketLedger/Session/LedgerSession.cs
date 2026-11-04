using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
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
        engine.CharacterKey = () => sync.Character?.Key;
        sync.ActiveSeen += PickUp;
        sync.FinalTaken += (id, status) =>
        {
            // The site has heard this run's end: it's nobody's to pick up again.
            if (RunWords.Finished(status) && config.RunInHand == id.ToString()) SetRunInHand(null);
        };
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
                SentAt = sentAt.TryGetValue(file.Guid, out var sent) ? sent : null,
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

    /// <summary>When each run started here was sent from the site (for the window).</summary>
    private readonly Dictionary<Guid, DateTime> sentAt = new();

    /// <summary>The run being taken from the site (Start pressed, the site not answered yet), or null.</summary>
    public Guid? Starting { get; private set; }

    /// <summary>
    /// Starts a run from the inbox. Everything that would stop it is checked first, then the site is asked
    /// for it (<see cref="SiteSync.Claim"/>: one plugin takes a run once, and not one stopped there); only when
    /// it says yes is a run in hand stopped (the window asks you before) and this one begun.
    /// </summary>
    public void Start(Guid runId)
    {
        var waiting = sync.Waiting.FirstOrDefault(w => w.Id == runId);
        if (waiting.File == null)
        {
            Notify("That run isn't in the inbox any more.");
            return;
        }
        if (Starting != null)
        {
            Notify("Another run is being taken from the site: a moment.");
            return;
        }
        if (engine.CanStart(waiting.File, overRunInHand: true) is { } why)
        {
            Notify($"\"{waiting.Name}\" can't start: {why}");
            return;
        }
        var (file, name, sent) = (waiting.File, waiting.Name, waiting.SentAt);
        Starting = runId;
        Notice = $"Taking \"{name}\" from the site…";
        Changed?.Invoke();
        sync.Claim(file, r =>
        {
            Starting = null;
            if (!r.Ok)
            {
                Notify($"\"{name}\" can't start: {r.Why ?? "the site didn't take it."}");
                sync.RunChanged();
                return;
            }
            Notice = null;
            later.Remove(runId);
            sentAt[runId] = sent;
            // Asked a moment ago: something may have come up meanwhile (combat, say). Then it's in hand, paused.
            var now = engine.CanStart(file, overRunInHand: true);
            engine.Start(file, now == null ? null : $"It couldn't start yet: {now} Resume when it can.");
            SetRunInHand(file.Id);
            sync.RunChanged();
            Changed?.Invoke();
        });
    }

    private void SetRunInHand(string? id)
    {
        var value = id ?? string.Empty;
        if (config.RunInHand == value) return;
        config.RunInHand = value;
        config.Save();
    }

    /// <summary>
    /// The first inbox after loading: the run this plugin had in hand (its id kept in the settings), if the
    /// site still has it going, is picked up again, paused; one the site can't hand back is said to be stopped.
    /// Runs taken by another install are left alone.
    /// </summary>
    private void PickUp(IReadOnlyList<ActiveRun> active)
    {
        if (engine.Active || config.RunInHand.Length == 0 || !Guid.TryParse(config.RunInHand, out var mine)) return;
        if (active.FirstOrDefault(a => a.Id == mine) is not { } run)
        {
            // Finished, or gone, on the site: nothing to pick up.
            SetRunInHand(null);
            return;
        }
        if (run.File is { } file && file.Guid == mine)
        {
            engine.Adopt(file, run.Progress, run.TakenKey);
            sync.RunChanged();
            Changed?.Invoke();
            return;
        }
        // The site didn't send the run itself: it can't be carried on, so the site hears it's stopped.
        var message = "Stopped: the plugin was reloaded and couldn't pick the run up again. Send it again from the site to carry on.";
        JsonObject? progress = null;
        if (run.Progress is { } p) progress = (p with { Status = RunStatus.Stopped, Message = message, UpdatedAt = DateTime.UtcNow }).ToJson();
        sync.QueueFinal(mine, mine.ToString(), RunStatus.Stopped, progress, run.TakenKey);
        SetRunInHand(null);
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
        // Linked again (even with the same token: a link the site turned down is tried afresh).
        lastToken = config.Token;
        sync.Relinked();
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------------------------------------
    // Every frame.

    private int engineSeen = -1;
    private int syncSeen = -1;

    private RunStatus lastStatus;

    public void Tick()
    {
        Safely(engine.Tick, "Market Ledger's run failed");
        Safely(sync.Tick, "Market Ledger couldn't keep in step with the site");
        Safely(StartLater, "Market Ledger couldn't start the next run");
        if (DateTime.UtcNow - helpersAt > TimeSpan.FromSeconds(30) && !engine.Active) CheckHelpers();
        if (engine.Version != engineSeen || sync.Version != syncSeen)
        {
            engineSeen = engine.Version;
            syncSeen = sync.Version;
            Changed?.Invoke();
        }
    }

    /// <summary>A run just finished (done): the one you said "after this run" to starts (the site asked first).</summary>
    private void StartLater()
    {
        var status = engine.File != null ? engine.Status : RunStatus.Accepted;
        var justDone = status == RunStatus.Done && lastStatus != RunStatus.Done;
        lastStatus = status;
        if (!justDone || Starting != null) return;
        if (RunPickup.NextLater(sync.Waiting.Select(w => w.Id), later) is { } next)
        {
            var name = sync.Waiting.First(w => w.Id == next).Name;
            engine.Say(HelperNames.Ledger, $"Next: \"{name}\", as you asked.");
            Start(next);
        }
    }

    private static void Safely(Action a, string what)
    {
        try
        {
            a();
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, what);
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
        Safely(() => Services.Chat.ChatMessage -= OnChat, "Market Ledger couldn't stop listening to chat");
        // A run in hand is left paused ("The plugin was unloaded."), and the site hears so (2 s at most): it's
        // picked up again when the plugin loads.
        Safely(() =>
        {
            if (engine.Unload() is { } last) sync.QueueFinal(last.File, last.Progress, engine.RunKey);
            sync.FlushOnUnload(TimeSpan.FromSeconds(2));
        }, "Market Ledger couldn't pause the run while unloading");
        Safely(engine.Dispose, "Market Ledger couldn't let go of the run's helpers");
        Safely(BoardListings.Unhook, "Market Ledger couldn't stop listening to the market board");
        Safely(sync.Dispose, "Market Ledger couldn't close its link to the site");
    }
}
