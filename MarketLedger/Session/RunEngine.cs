using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using MarketLedger.Automation;
using MarketLedger.Core;
using MarketLedger.Game;
using MarketLedger.Ipc;

namespace MarketLedger.Session;

/// <summary>
/// Carries a run out, step by step, on the framework thread (one <see cref="Tick"/> a frame), the way
/// Housing Studio's plugin's errand runner does: one helper driving at a time (Artisan crafts,
/// GatherBuddy Reborn gathers, and the plugin's own clicks take from retainers, buy and entrust, with
/// Lifestream and vnavmesh to get about), every wait with a timeout, every action logged.
///
/// What a step has done is read from your bags (each line says how many must be there once it's done),
/// so a step paused or stopped halfway starts again from where your bags are. Anything it didn't expect
/// pauses the run and says why: combat, a duty or a duty pop, a cutscene, logging out, a zone change no
/// travel asked for, a tell (if you like), full bags, too little gil, a price above the line's, the gil
/// agreed for the run, a window that doesn't open, a helper that stops answering. Resume starts the step
/// again; Skip goes on to the next; Stop ends the run and lets go of everything it started.
/// </summary>
public sealed partial class RunEngine : IDisposable
{
    private static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan YouWait = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan WalkWait = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan TravelWait = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan WorldWait = TimeSpan.FromMinutes(5);

    /// <summary>How close is close enough to use something; how near an NPC before talking to them.</summary>
    private const float Reach = 3f;

    private const float NpcReach = 2f;

    /// <summary>A gil shop sells at most this many in one purchase.</summary>
    private const int MaxPerPurchase = 99;

    /// <summary>A click not yet seen working: held for you until you trust the new clicks.</summary>
    private const string Guess = "guess";

    private readonly Configuration config;
    private readonly Vendors vendors;
    private readonly StepQueue queue = new(() => DateTime.UtcNow);
    private readonly RunLog log = new();

    public RunEngine(Configuration config, Vendors vendors)
    {
        this.config = config;
        this.vendors = vendors;
        queue.StepsPerTick = 4;
        queue.ActionGap = TimeSpan.FromMilliseconds(300);
        queue.Trace += (step, what) =>
        {
            if (what != "start" && what != "done") Say(HelperNames.Ledger, $"✕ {what}");
        };
        queue.Timed += (step, took) =>
        {
            if (step.IsAction) Services.Log.Debug($"Market Ledger: → {step.Label} ({took.TotalMilliseconds:0} ms)");
        };
    }

    public Artisan Artisan { get; } = new();
    public GatherBuddy GatherBuddy { get; } = new();
    public Vnavmesh Nav { get; } = new();
    public Lifestream Travel { get; } = new();
    public AutoRetainer AutoRetainer { get; } = new();

    // ---------------------------------------------------------------------------------------------
    // What the window and the site see.

    public RunFile? File { get; private set; }

    public RunStatus Status { get; private set; } = RunStatus.Accepted;

    public int StepIndex { get; private set; }

    private StepState[] states = Array.Empty<StepState>();
    private string?[] notes = Array.Empty<string?>();

    public IReadOnlyList<StepState> States => states;

    public IReadOnlyList<string?> Notes => notes;

    /// <summary>Gil spent so far, vendors and the market together (and, for a run picked up again, what it had spent before).</summary>
    public long Spent => VendorSpent + MarketSpent;

    public long VendorSpent { get; private set; }

    public long MarketSpent { get; private set; }

    public DateTime? StartedAt { get; private set; }

    public string? Message { get; private set; }

    public string? Doing { get; private set; }

    public bool HeldForYou { get; private set; }

    public string? CopyText { get; private set; }

    public IReadOnlyList<ProgressLog> Log => log.Lines;

    /// <summary>A run is in hand and not finished.</summary>
    public bool Active => File != null && !RunWords.Finished(Status);

    /// <summary>The key of the character you're playing (the site's id for it), or null when not known; set by the session.</summary>
    public Func<string?> CharacterKey { get; set; } = () => null;

    /// <summary>The runs this plugin has had in hand since it loaded: never offered or started again.</summary>
    public IReadOnlyCollection<Guid> Taken => taken;

    private readonly HashSet<Guid> taken = new();

    /// <summary>The character (content id) the run is being carried out by: another one logged in pauses it.</summary>
    private ulong runBy;

    /// <summary>The key of the character carrying the run out (the site's <c>p_key</c>, its <c>taken_key</c>), or null when not known.</summary>
    public string? RunKey { get; private set; }

    /// <summary>What each market line has spent this run (its budget holds across pauses and resumes).</summary>
    private readonly Dictionary<RunLine, long> lineSpent = new(ReferenceEqualityComparer.Instance);

    /// <summary>A site command not obeyed (Settings), said in the progress for a while.</summary>
    private (string Text, DateTime Until)? siteNote;

    /// <summary>A run came to its end (done, stopped or failed): its last progress, for the site.</summary>
    public event Action<RunFile, RunProgress>? Ended;

    /// <summary>Goes up whenever anything above changes (the session makes its RunState afresh).</summary>
    public int Version { get; private set; }

    /// <summary>The status or the step changed: the site should hear at once (not just on the 15 s beat).</summary>
    public event Action? Changed;

    private void Touch(bool important = false)
    {
        Version++;
        if (important) Changed?.Invoke();
    }

    /// <summary>A line in the run's log (and Dalamud's), by who did it.</summary>
    public void Say(string who, string text)
    {
        log.Add(DateTime.UtcNow, who, text);
        Services.Log.Information($"Market Ledger [{who}] {text}");
        Touch();
    }

    /// <summary>
    /// How many of a line's item your bags hold now, HQ and NQ together, as the site counts a line's need
    /// (a market line wanting HQ buys HQ listings only, so what it adds is HQ).
    /// </summary>
    private static int Have(RunStepKind kind, RunLine line) => InGame.BagCount(line.Item);

    private RunStep? Current => File != null && StepIndex >= 0 && StepIndex < File.Steps.Count ? File.Steps[StepIndex] : null;

    /// <summary>Each step's progress, as your bags say it now (a finished step keeps what it had).</summary>
    public List<ProgressStep> StepsNow()
    {
        var list = new List<ProgressStep>();
        if (File == null) return list;
        for (var i = 0; i < File.Steps.Count; i++)
        {
            var s = File.Steps[i];
            var (done, of) = StepMath.Progress(s, l => Have(s.Kind, l));
            if (states[i] == StepState.Done) done = Math.Max(done, of);
            list.Add(new ProgressStep(s.Id, states[i], done, of, notes[i]));
        }
        return list;
    }

    /// <summary>The progress as the site takes it (<c>ml_plugin_run_update</c>).</summary>
    public RunProgress Progress()
    {
        var note = siteNote is { } n && DateTime.UtcNow < n.Until ? n.Text : null;
        return new(Status, Math.Clamp(StepIndex, 0, Math.Max(0, (File?.Steps.Count ?? 1) - 1)), StepsNow(), Spent, StartedAt, DateTime.UtcNow,
            log.Lines.Take(RunProgress.LogLines).ToList(), Message ?? note)
        {
            Doing = Doing,
            HeldForYou = HeldForYou,
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Starting, pausing, stopping.

    /// <summary>Why a run can't start now, or null when it can.</summary>
    public string? CanStart(RunFile file, bool overRunInHand = false)
    {
        if (Active && !overRunInHand) return "Another run is in hand: stop it first.";
        if (taken.Contains(file.Guid) && File?.Guid != file.Guid) return "This run has been in hand already: send it again from the site to run it again.";
        if (!Services.ClientState.IsLoggedIn || Services.Objects.LocalPlayer == null) return "Log in first.";
        if (OtherCharacter(file) is { } who) return who;
        if (InGame.Blocked(false) is { } why) return why;
        var kinds = file.Steps.Select(s => s.Kind).ToHashSet();
        if (kinds.Contains(RunStepKind.Craft) && !Artisan.Installed) return "The run crafts: install Artisan first.";
        if (kinds.Contains(RunStepKind.Gather) && !GatherBuddy.Installed) return "The run gathers: install GatherBuddy Reborn first.";
        if (kinds.Overlaps(new[] { RunStepKind.Vendor, RunStepKind.Market, RunStepKind.Withdraw, RunStepKind.Entrust }))
        {
            if (!Nav.Installed) return "The run walks to vendors, bells and boards: install vnavmesh first.";
            if (!Travel.Installed) return "The run travels: install Lifestream first.";
        }
        return null;
    }

    /// <summary>Why the run isn't this character's (null when it is, or when the run names nobody).</summary>
    private string? OtherCharacter(RunFile file)
    {
        if (string.IsNullOrEmpty(file.Character)) return null;
        var me = CharacterKey();
        if (me == null) return "Your character isn't read yet: try again in a moment.";
        return string.Equals(me, file.Character, StringComparison.Ordinal) ? null
            : "It was planned for another of your characters (its bags, retainers and gil). Log in with that one, or plan it again for this one.";
    }

    /// <summary>
    /// Starts a run (the site has given it to this plugin: <see cref="SiteSync.Claim"/>). With
    /// <paramref name="pausedWhy"/>, it's in hand but paused, saying why (something came up while the site
    /// was asked): Resume starts it.
    /// </summary>
    public void Start(RunFile file, string? pausedWhy = null)
    {
        if (Active) Stop($"Put aside for \"{file.Name}\".");
        Take(file);
        states = file.Steps.Select(_ => StepState.Waiting).ToArray();
        notes = new string?[file.Steps.Count];
        StepIndex = 0;
        StartedAt = DateTime.UtcNow;
        Say(HelperNames.Ledger, $"Started \"{file.Name}\": {file.Steps.Count} steps, up to {file.MaxGil:N0} gil agreed.");
        if (pausedWhy != null)
        {
            Status = RunStatus.Paused;
            pausedAt = DateTime.UtcNow;
            Message = pausedWhy;
            Say(HelperNames.Ledger, $"Paused: {pausedWhy}");
            Touch(true);
            return;
        }
        Status = RunStatus.Running;
        HoldHelpers();
        BeginStep(NextFrom(0));
        Touch(true);
    }

    /// <summary>
    /// A run this plugin had in hand when it was unloaded, picked up again from the progress the site kept:
    /// paused (your bags say where each step is; Resume carries on). What it had spent still counts against
    /// the gil agreed.
    /// </summary>
    public void Adopt(RunFile file, RunProgress? progress, string? takenKey = null)
    {
        if (Active) return;
        Take(file);
        // The character it was being carried out by, as the site has it: only that one carries it on.
        if (!string.IsNullOrEmpty(takenKey)) RunKey = takenKey;
        var (s, n, step) = RunPickup.Restore(file, progress);
        states = s;
        notes = n;
        StepIndex = step;
        // Spent before counts against the run's gil, and (to be safe) against your market ceiling too.
        MarketSpent = Math.Max(0, progress?.Spent ?? 0);
        StartedAt = progress?.StartedAt ?? DateTime.UtcNow;
        foreach (var l in (progress?.Log ?? Array.Empty<ProgressLog>()).Reverse()) log.Add(l.At, l.Who, l.Text);
        Status = RunStatus.Paused;
        pausedAt = DateTime.UtcNow;
        Message = "Picked up again after the plugin loaded. Press Resume to carry on from what your bags hold.";
        Say(HelperNames.Ledger, $"Picked up \"{file.Name}\" again ({Spent:N0} gil spent before): paused until you Resume.");
        Touch(true);
    }

    private void Take(RunFile file)
    {
        File = file;
        taken.Add(file.Guid);
        lineSpent.Clear();
        VendorSpent = MarketSpent = 0;
        Message = null;
        Doing = null;
        HeldForYou = false;
        CopyText = null;
        siteNote = null;
        gatherHeld = false;
        log.Clear();
        lastTerritory = InGame.Territory;
        runBy = Services.PlayerState.ContentId;
        RunKey = CharacterKey();
    }

    /// <summary>AutoRetainer held for the run, when that's asked for (on starting, and on resuming a run picked up again).</summary>
    private void HoldHelpers()
    {
        if (config.HoldAutoRetainer && AutoRetainer.Installed && !AutoRetainer.Held)
            Say(HelperNames.AutoRetainer, AutoRetainer.Hold() ? "Held until the run's done." : "Didn't answer: not held.");
    }

    /// <summary>Pauses: whatever a helper is doing stops, the step starts again on Resume.</summary>
    public void Pause(string why)
    {
        if (!Active || Status == RunStatus.Paused) return;
        HaltStep();
        Status = RunStatus.Paused;
        pausedAt = DateTime.UtcNow;
        Message = why;
        HeldForYou = false;
        Doing = null;
        Say(HelperNames.Ledger, $"Paused: {why}");
        if (config.ChatMessages) Services.Chat.Print($"Paused: {why}", "Market Ledger");
        Touch(true);
    }

    /// <summary>
    /// Carries on: from a pause, the step starts again (from what your bags hold); while it waits for you
    /// (a click not yet seen working, a list to import), it's the go-ahead.
    /// </summary>
    public void Resume()
    {
        if (!Active) return;
        if (Status == RunStatus.Waiting)
        {
            if (queue.State == QueueState.AwaitingContinue)
            {
                Say(HelperNames.Ledger, "Going ahead, as you said.");
                queue.Continue();
            }
            else if (gatherHeld)
            {
                gatherHeld = false;
                Say(HelperNames.Ledger, "Going on with GatherBuddy Reborn's list, as you said.");
            }
            Status = RunStatus.Running;
            HeldForYou = false;
            Message = null;
            Touch(true);
            return;
        }
        if (Status != RunStatus.Paused) return;
        var blocked = !Services.ClientState.IsLoggedIn || Services.Objects.LocalPlayer == null ? "log in first."
            : File != null && OtherCharacter(File) is { } other ? other
            : RunKey != null && CharacterKey() is { } me && me != RunKey ? "another of your characters was carrying this run out: log in with that one."
            : InGame.Blocked(false);
        if (blocked != null)
        {
            Message = $"Can't resume yet: {blocked}";
            Touch(true);
            return;
        }
        Status = RunStatus.Running;
        Message = null;
        pausedAt = null;
        lastTerritory = InGame.Territory;
        runBy = Services.PlayerState.ContentId;
        RunKey ??= CharacterKey();
        HoldHelpers();
        Say(HelperNames.Ledger, "Resumed.");
        BeginStep(NextFrom(StepIndex));
        Touch(true);
    }

    /// <summary>Leaves the step as it is and goes on to the next.</summary>
    public void Skip()
    {
        if (!Active || Current is not { } step) return;
        HaltStep();
        states[StepIndex] = StepState.Skipped;
        Say(HelperNames.Ledger, $"Skipped: {StepMath.Label(step, InGame.ItemName)}.");
        var wasPaused = Status == RunStatus.Paused;
        Status = RunStatus.Running;
        Message = null;
        HeldForYou = false;
        if (wasPaused) lastTerritory = InGame.Territory;
        BeginStep(NextFrom(StepIndex + 1));
        Touch(true);
    }

    /// <summary>Ends the run: walking, travel, gathering and crafting stop, the windows it opened close, AutoRetainer is let be.</summary>
    public void Stop(string why)
    {
        if (!Active) return;
        HaltStep();
        if (Current != null && states[StepIndex] == StepState.Running) states[StepIndex] = StepState.Failed;
        Status = RunStatus.Stopped;
        Message = why;
        Doing = null;
        HeldForYou = false;
        CopyText = null;
        LetGo();
        Say(HelperNames.Ledger, $"Stopped: {why} ({Spent:N0} gil spent).");
        if (config.ChatMessages) Services.Chat.PrintError($"Stopped: {why}", "Market Ledger");
        Touch(true);
        RaiseEnded();
    }

    private void RaiseEnded()
    {
        if (File is not { } file) return;
        try
        {
            Ended?.Invoke(file, Progress());
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "Market Ledger couldn't note the run's end for the site");
        }
    }

    private void Finish()
    {
        Status = RunStatus.Done;
        Message = null;
        Doing = null;
        HeldForYou = false;
        CopyText = null;
        LetGo();
        var short_ = states.Count(st => st == StepState.Failed);
        Say(HelperNames.Ledger, short_ == 0 ? $"Done: every step's through, {Spent:N0} gil spent." : $"Done, {short_} step{(short_ == 1 ? string.Empty : "s")} short (see their notes); {Spent:N0} gil spent.");
        if (config.ChatMessages) Services.Chat.Print($"\"{File?.Name}\" is done.", "Market Ledger");
        Touch(true);
        RaiseEnded();
    }

    /// <summary>Puts the run in hand away (after it's finished, or for another).</summary>
    public void Clear()
    {
        if (Active) return;
        File = null;
        states = Array.Empty<StepState>();
        notes = Array.Empty<string?>();
        Touch(true);
    }

    /// <summary>Lets go of everything a run took hold of.</summary>
    private void LetGo()
    {
        AutoRetainer.LetBe();
        RestoreGatherLists();
    }

    /// <summary>Stops whatever the current step started: walking, travel, helpers, the clicks' windows.</summary>
    private void HaltStep()
    {
        if (queue.State is QueueState.Running or QueueState.AwaitingContinue) queue.Abort("Stopped.");
        Nav.Stop();
        walkTarget = null;
        if (travelling || Travel.Busy) Travel.Abort();
        travelling = false;
        if (Current?.Kind == RunStepKind.Gather || gatherStarted)
        {
            if (GatherBuddy.SetEnabled(false)) Say(HelperNames.GatherBuddy, "Auto-gather off.");
            gatherStarted = false;
        }
        if (Current?.Kind == RunStepKind.Craft && craftStarted)
        {
            if (Artisan.StopEndurance()) Say(HelperNames.Artisan, "Asked to stop after the craft under way.");
            craftStarted = false;
        }
        CloseWindows();
        segments.Clear();
        segment = -1;
        gatherHeld = false;
    }

    // ---------------------------------------------------------------------------------------------
    // Steps.

    private int NextFrom(int from) => StepMath.NextStep(states, from);

    private void BeginStep(int index)
    {
        if (File == null) return;
        if (index < 0)
        {
            Finish();
            return;
        }
        StepIndex = index;
        var step = File.Steps[index];
        states[index] = StepState.Running;
        notes[index] = null;
        CopyText = null;
        HeldForYou = false;
        slotsAtStepStart = InGame.FreeSlots();
        Touch(true);
        if (StepMath.StepDone(step, l => Have(step.Kind, l)))
        {
            Say(HelperNames.Ledger, $"{StepMath.Label(step, InGame.ItemName)}: your bags have it all already.");
            EndStep();
            return;
        }
        Say(HelperNames.Ledger, $"Step {index + 1}: {StepMath.Label(step, InGame.ItemName)}.");
        switch (step.Kind)
        {
            case RunStepKind.Gather:
                BeginGather(step);
                break;
            case RunStepKind.Craft:
                BeginCraft(step);
                break;
            default:
                BeginClicks(step);
                break;
        }
    }

    /// <summary>The step is through: on to the next (or the end).</summary>
    private void EndStep()
    {
        if (Current is not { } step) return;
        states[StepIndex] = StepState.Done;
        var (done, of) = StepMath.Progress(step, l => Have(step.Kind, l));
        Say(HelperNames.Ledger, $"Step {StepIndex + 1} done ({done} of {of}).");
        Doing = null;
        BeginStep(NextFrom(StepIndex + 1));
    }

    /// <summary>
    /// The step is as far as it can go and fell short (a craft whose materials ran out: the site leaves
    /// out what nobody can get): marked failed with why, and the run goes on to the next step.
    /// </summary>
    private void FailStep(string why)
    {
        if (Current is not { } step) return;
        HaltStep();
        states[StepIndex] = StepState.Failed;
        notes[StepIndex] = why;
        Doing = null;
        Say(HelperNames.Ledger, $"Step {StepIndex + 1} fell short: {why}");
        if (config.ChatMessages) Services.Chat.PrintError($"{StepMath.Label(step, InGame.ItemName)} fell short: {why}", "Market Ledger");
        BeginStep(NextFrom(StepIndex + 1));
        Touch(true);
    }

    /// <summary>The step can't go on: the run pauses, saying why (Resume tries the step again, Skip goes on).</summary>
    private void StepProblem(string why)
    {
        if (StepIndex >= 0 && StepIndex < notes.Length) notes[StepIndex] = why;
        Pause(why);
    }

    // ---------------------------------------------------------------------------------------------
    // Every frame.

    private DateTime? pausedAt;
    private uint lastTerritory;
    private bool travelling;
    private int slotsAtStepStart;

    public void Tick()
    {
        if (!Active) return;
        try
        {
            TickSafely();
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Market Ledger's run failed");
            // Failed first, so a let-go that throws too never has this run tried again every frame.
            Status = RunStatus.Failed;
            Message = $"Something went wrong ({ex.Message}); stopped to be safe.";
            Doing = null;
            HeldForYou = false;
            Quietly(HaltStep);
            Quietly(LetGo);
            Say(HelperNames.Ledger, Message);
            Touch(true);
            RaiseEnded();
        }
    }

    private static void Quietly(Action a)
    {
        try
        {
            a();
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "Market Ledger couldn't let go of something");
        }
    }

    private void TickSafely()
    {
        if (Status == RunStatus.Paused) return;
        var step = Current;
        // Gathering travels by itself (GatherBuddy Reborn teleports to its nodes).
        var travelOk = travelling || step?.Kind == RunStepKind.Gather;
        if (!Services.ClientState.IsLoggedIn)
        {
            Pause("You logged out.");
            return;
        }
        if (InGame.Blocked(travelOk) is { } why)
        {
            Pause(why);
            return;
        }
        if (runBy != 0 && Services.PlayerState.ContentId is var me && me != 0 && me != runBy)
        {
            Pause("Another character is logged in.");
            return;
        }
        var t = InGame.Territory;
        if (t != lastTerritory)
        {
            lastTerritory = t;
            if (!travelOk)
            {
                Pause("You changed zones when no travel was asked for.");
                return;
            }
        }
        if (step == null)
        {
            Finish();
            return;
        }
        switch (step.Kind)
        {
            case RunStepKind.Gather:
                TickGather(step);
                break;
            case RunStepKind.Craft:
                TickCraft(step);
                break;
            default:
                TickClicks(step);
                break;
        }
    }

    /// <summary>A tell came (the session listens to chat): pauses when you asked for that.</summary>
    public void Tell(string from)
    {
        if (!config.PauseOnTell || !Active || Status is RunStatus.Paused) return;
        Pause($"{(from.Length > 0 ? from : "Someone")} sent you a tell.");
    }

    /// <summary>What the site asked (<c>ml_plugin_run_update</c>'s command), when you let it.</summary>
    public void Command(RunCommand command)
    {
        if (!Active) return;
        if (!config.LetSiteControl)
        {
            var text = $"The site asked to {RunWords.Of(command)}; that's turned off in the plugin's Settings (Let the site pause and stop).";
            Say(HelperNames.Ledger, text);
            siteNote = (text, DateTime.UtcNow + TimeSpan.FromMinutes(2));
            Touch(true);
            return;
        }
        switch (command)
        {
            case RunCommand.Pause:
                Pause("Paused from the site.");
                break;
            case RunCommand.Resume when HeldForYou:
                // What's held is held for someone watching the game (a click not yet seen working, a list to import).
                var held = "The site asked to resume; what the run's waiting for needs you in the game: press Resume there.";
                Say(HelperNames.Ledger, held);
                siteNote = (held, DateTime.UtcNow + TimeSpan.FromMinutes(2));
                Touch(true);
                break;
            case RunCommand.Resume:
                Resume();
                break;
            case RunCommand.Skip:
                Skip();
                break;
            case RunCommand.Stop:
                Stop("Stopped from the site.");
                break;
        }
    }

    /// <summary>About how long is left: the site's estimate for the steps not done, scaled by how much of the current one is.</summary>
    public TimeSpan? Left()
    {
        if (File == null || File.Estimate.Minutes <= 0 || File.Steps.Count == 0) return null;
        var steps = StepsNow();
        double total = 0, done = 0;
        foreach (var s in steps)
        {
            total += Math.Max(1, s.Of);
            done += s.State is StepState.Done or StepState.Skipped ? Math.Max(1, s.Of) : Math.Min(s.Done, Math.Max(1, s.Of));
        }
        if (total <= 0) return null;
        var fraction = Math.Clamp(done / total, 0, 1);
        return TimeSpan.FromMinutes(File.Estimate.Minutes * (1 - fraction));
    }

    /// <summary>
    /// The plugin is going: whatever the run started stops, and it's left paused ("The plugin was unloaded."),
    /// so the plugin picks it up again when it loads. Its progress then, for the site's last word (null when no
    /// run was in hand).
    /// </summary>
    public (RunFile File, RunProgress Progress)? Unload()
    {
        unloaded = true;
        if (!Active || File is not { } file)
        {
            Quietly(LetGo);
            return null;
        }
        Quietly(HaltStep);
        Quietly(LetGo);
        Status = RunStatus.Paused;
        Message = "The plugin was unloaded.";
        Doing = null;
        HeldForYou = false;
        Say(HelperNames.Ledger, "Paused: the plugin was unloaded.");
        return (file, Progress());
    }

    private bool unloaded;

    public void Dispose()
    {
        if (!unloaded) Unload();
    }

    private static void CloseWindows()
    {
        // A purchase's question first: said no to (closed), never left up under what comes next.
        foreach (var name in new[] { UiTable.YesNo, UiTable.ContextMenu, ErrandTable.Shop, ErrandTable.ItemSearchResult, ErrandTable.ItemSearch, ErrandTable.SelectIconString })
            GameUi.Close(name);
        foreach (var name in ErrandTable.RetainerInventory) GameUi.Close(name);
        // The retainer's menu and list last (closing the menu says goodbye to the retainer).
        GameUi.Close(ErrandTable.SelectString);
        GameUi.Close(ErrandTable.RetainerList);
    }

    private Vector3? walkTarget;
}
